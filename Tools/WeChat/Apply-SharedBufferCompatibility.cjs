// Adapt generated Emscripten checks to WeChat Worker V2's cross-context buffers.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const vm = require('node:vm');

const files = [
  'minigame/webgl.wasm.framework.unityweb.js',
  'minigame/workers/response/webgl.wasm.framework.unityweb.js',
  'webgl/Build/webgl.framework.js',
];
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));

function patchSource(source) {
  let count = 0;
  const result = source.replace(/\b(heapOrArray\.buffer|wasmMemory\.buffer|buffer) instanceof SharedArrayBuffer\b/g, (_, buffer) => {
    count++;
    // The DevTools upload parser rejects optional chaining in generated frameworks.
    return `(${buffer} != null && ${buffer}.constructor != null && ${buffer}.constructor.name === 'SharedArrayBuffer')`;
  });
  if (count !== 5 || /instanceof\s+SharedArrayBuffer\b/.test(result)) {
    throw new Error(`Generated SAB checks changed: expected 5 recognized checks, got ${count}`);
  }
  new vm.Script(result);
  return {source: result, replacements: count};
}

function apply(root, rollback = false) {
  root = path.resolve(root);
  const recordFile = path.join(root, 'shared-buffer-compatibility.json');
  const existing = fs.existsSync(recordFile) ? readJson(recordFile) : null;
  if (existing) {
    if (existing.schema !== 1 || JSON.stringify(existing.files.map(f => f.path)) !== JSON.stringify(files)) {
      throw new Error('Unexpected SAB compatibility record');
    }
    for (const file of existing.files) {
      const expected = existing.status === 'restored' ? file.originalSha256 : file.patchedSha256;
      if (sha(fs.readFileSync(path.join(root, file.path))) !== expected) {
        throw new Error(`Framework changed since SAB patch; refusing to overwrite: ${file.path}`);
      }
    }
    if (!rollback && existing.status === 'applied') return existing;
    if (rollback && existing.status === 'restored') return existing;
  } else if (rollback) {
    throw new Error('No SAB patch record to restore');
  }
  const manifest = readJson(path.join(root, 'profiler-build.json'));
  if (!manifest.threads) throw new Error('Expected a native threads export');
  const backupRoot = path.join(root, 'profiler-backups/shared-buffer-compatibility');
  // Validate all inputs and syntax before changing any generated file.
  const plans = files.map(relative => {
    const target = path.join(root, relative);
    const original = fs.readFileSync(target);
    const backup = path.join(backupRoot, relative);
    if (rollback) {
      const restored = fs.readFileSync(backup);
      const info = existing.files.find(f => f.path === relative);
      if (sha(restored) !== info.originalSha256) throw new Error(`SAB backup hash mismatch: ${relative}`);
      return {target, data: restored};
    }
    const patched = patchSource(original.toString('utf8'));
    if (fs.existsSync(backup) && sha(fs.readFileSync(backup)) !== sha(original)) {
      throw new Error(`SAB backup differs: ${relative}`);
    }
    return {target, backup, original, data: Buffer.from(patched.source), info: {
      path: relative, replacements: patched.replacements,
      originalSha256: sha(original), patchedSha256: sha(patched.source),
    }};
  });
  for (const plan of plans) {
    if (!rollback && !fs.existsSync(plan.backup)) {
      fs.mkdirSync(path.dirname(plan.backup), {recursive: true});
      fs.writeFileSync(plan.backup, plan.original, {flag: 'wx'});
    }
  }
  for (const plan of plans) fs.writeFileSync(plan.target, plan.data);
  const record = rollback ? {...existing, status: 'restored'} : {
    schema: 1, buildId: manifest.buildId, status: 'applied', files: plans.map(p => p.info),
    note: 'Type-check compatibility only; no SAB constructor or worker capability is supplied. Phone Jobs validation remains required.',
  };
  record.updatedUtc = new Date().toISOString();
  fs.writeFileSync(recordFile, JSON.stringify(record, null, 2) + '\n');
  return record;
}

if (require.main === module) {
  if (!process.argv[2] || process.argv.slice(3).some(arg => arg !== '--rollback')) {
    throw new Error('Usage: node Apply-SharedBufferCompatibility.cjs EXPORT_ROOT [--rollback]');
  }
  console.log(JSON.stringify(apply(process.argv[2], process.argv.includes('--rollback')), null, 2));
}
module.exports = {patchSource, apply, files};
