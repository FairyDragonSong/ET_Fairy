// Apply an already optimized Wasm to an export, preserving its original code outside minigame.
// Usage: node Apply-WasmCompatibility.cjs EXPORT_ROOT OPTIMIZED_WASM
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const zlib = require('zlib');
// Read interfaces without JIT compiling the original, very large debug module.
function interfaces(bytes) {
  let pos = 8;
  const uint = () => { let value = 0, shift = 0, c; do { c = bytes[pos++]; value += (c & 127) * 2 ** shift; shift += 7; } while (c & 128); return value; };
  const text = () => { const n = uint(), start = pos; pos += n; return bytes.toString('utf8', start, pos); };
  const limits = () => { const flags = uint(); uint(); if (flags & 1) uint(); };
  const result = { imports: [], exports: [], hasName: false };
  const kinds = ['function', 'table', 'memory', 'global', 'tag'];
  while (pos < bytes.length) {
    const id = bytes[pos++], length = uint(), end = pos + length;
    if (id === 0) { const name = text(); if (name === 'name') result.hasName = true; }
    if (id === 2) {
      const count = uint();
      for (let i = 0; i < count; i++) {
        const module = text(), name = text(), kind = bytes[pos++];
        result.imports.push({ module, name, kind: kinds[kind] });
        if (kind === 0) uint();
        else if (kind === 1) { pos++; limits(); }
        else if (kind === 2) limits();
        else if (kind === 3) pos += 2;
        else if (kind === 4) { uint(); uint(); }
        else throw new Error('Unknown import kind');
      }
    }
    if (id === 7) {
      const count = uint();
      for (let i = 0; i < count; i++) { const name = text(), kind = bytes[pos++]; uint(); result.exports.push({ name, kind: kinds[kind] }); }
    }
    pos = end;
  }
  for (const key of ['imports', 'exports']) result[key].sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)));
  return result;
}
async function main() {
  const root = path.resolve(process.argv[2]);
  const input = path.resolve(process.argv[3]);
  const gamePath = path.join(root, 'minigame/game.js');
  const manifestPath = path.join(root, 'profiler-build.json');
  const game = fs.readFileSync(gamePath, 'utf8');
  const manifestText = fs.readFileSync(manifestPath, 'utf8').replace(/^\uFEFF/, '');
  const manifest = JSON.parse(manifestText);
  if (manifest.wasmCompatibility) throw new Error('Already processed; restore the original export before repeating.');
  const match = [...game.matchAll(/CODE_FILE_MD5:\s*'([^']+)'/g)];
  if (match.length !== 1) throw new Error('Ambiguous CODE_FILE_MD5');
  const codeDir = path.join(root, 'minigame/wasmcode');
  const codeFiles = fs.readdirSync(codeDir).filter(n => n.startsWith(match[0][1] + '.') && n.endsWith('.wasm.br'));
  if (codeFiles.length !== 1) throw new Error('Expected one matching original Wasm');
  const original = fs.readFileSync(path.join(root, 'webgl/Build/webgl.wasm'));
  const optimized = fs.readFileSync(input);
  console.log('Validating optimized WebAssembly and comparing named interfaces...');
  if (!WebAssembly.validate(optimized)) throw new Error('Optimized module validation failed');
  const before = interfaces(original), after = interfaces(optimized);
  const key = item => JSON.stringify(item);
  const addedImports = after.imports.filter(item => !before.imports.some(old => key(old) === key(item)));
  const removedImports = before.imports.filter(item => !after.imports.some(now => key(now) === key(item)));
  // DCE may drop unused imports. The existing JS import object can still supply
  // them; new required imports or changed exported names/kinds are rejected.
  if (addedImports.length || JSON.stringify(before.exports) !== JSON.stringify(after.exports))
    throw new Error('New required imports or changed exported names/kinds');
  if (after.hasName) throw new Error('Debug name section remains');
  console.log('Exports preserved; no new imports; removed imports: ' + JSON.stringify(removedImports) + '; compressing...');
  const compressed = zlib.brotliCompressSync(optimized, { params: {
    [zlib.constants.BROTLI_PARAM_QUALITY]: 11,
    [zlib.constants.BROTLI_PARAM_SIZE_HINT]: optimized.length
  }});
  if (!zlib.brotliDecompressSync(compressed).equals(optimized)) throw new Error('Brotli roundtrip failed');
  const digest = (data, kind = 'sha256') => crypto.createHash(kind).update(data).digest('hex');
  const id = digest(compressed, 'md5').slice(0, 16);
  const newName = id + codeFiles[0].slice(match[0][1].length);
  const backup = path.join(root, 'profiler-backups/wasm-compatibility');
  fs.mkdirSync(backup, { recursive: true });
  for (const n of ['game.js', 'profiler-build.json', codeFiles[0]])
    if (fs.existsSync(path.join(backup, n))) throw new Error('Original backup already exists: ' + n);
  fs.copyFileSync(gamePath, path.join(backup, 'game.js'));
  fs.copyFileSync(manifestPath, path.join(backup, 'profiler-build.json'));
  fs.copyFileSync(path.join(codeDir, codeFiles[0]), path.join(backup, codeFiles[0]));
  fs.writeFileSync(path.join(codeDir, newName), compressed);
  manifest.wasmCompatibility = {
    utc: new Date().toISOString(), command: '-Oz --strip-debug --enable-sign-ext --enable-nontrapping-float-to-int --enable-bulk-memory' + (manifest.simd ? ' --enable-simd' : '') + (manifest.threads ? ' --enable-threads' : ''),
    originalBytes: original.length, optimizedBytes: optimized.length, compressedBytes: compressed.length,
    originalSha256: digest(original), optimizedSha256: digest(optimized), compressedSha256: digest(compressed),
    originalCodeId: match[0][1], codeId: id, exportsNamesKindsMatch: true, addedImports, removedImports, brotliRoundtrip: true,
    note: 'Build flags preserved; development=' + manifest.development + ', autoConnect=' + manifest.autoConnect + '. Wasm postprocessing changes timing and debug symbols. Runtime launch and performance need verification.'
  };
  manifest.note = (manifest.development ? 'Development' : 'Release') + ' export, then wasm-opt -Oz --strip-debug compatibility postprocessing; see wasmCompatibility. Runtime launch and performance must be verified separately.';
  fs.writeFileSync(gamePath, game.replace(/CODE_FILE_MD5:\s*'([^']+)'/, "CODE_FILE_MD5: '" + id + "'"));
  fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2) + '\n');
  // Original is backed up outside the packaged minigame before removing its duplicate.
  fs.unlinkSync(path.join(codeDir, codeFiles[0]));
  fs.copyFileSync(input, path.join(root, 'profiler-build-evidence/compatibility-optimized.wasm'));
  fs.writeFileSync(path.join(root, 'profiler-build-evidence/wasm-compatibility.json'), JSON.stringify(manifest.wasmCompatibility, null, 2) + '\n');
  console.log(JSON.stringify(manifest.wasmCompatibility, null, 2));
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
