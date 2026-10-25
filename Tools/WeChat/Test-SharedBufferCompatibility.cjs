const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const vm = require('node:vm');
const {execFileSync} = require('node:child_process');
const {patchSource, apply, files} = require('./Apply-SharedBufferCompatibility.cjs');

const fixture = `
function check(buffer) { return buffer instanceof SharedArrayBuffer; }
function memory(wasmMemory) { return wasmMemory.buffer instanceof SharedArrayBuffer; }
function heap(heapOrArray) { return heapOrArray.buffer instanceof SharedArrayBuffer; }
function first(buffer) { return buffer instanceof ArrayBuffer || buffer instanceof SharedArrayBuffer; }
function second(buffer) { return buffer instanceof ArrayBuffer || buffer instanceof SharedArrayBuffer; }
`;

test('cross-context shared memory works with no global SAB constructor', () => {
  const context = vm.createContext({SharedArrayBuffer: undefined});
  vm.runInContext(patchSource(fixture).source, context);
  context.shared = new WebAssembly.Memory({initial: 1, maximum: 1, shared: true}).buffer;
  assert.equal(vm.runInContext('check(shared)', context), true);
  assert.equal(vm.runInContext('check(new ArrayBuffer(8))', context), false);
  assert.equal(vm.runInContext('check(null)', context), false);
  assert.equal(vm.runInContext('check(undefined)', context), false);
  assert.equal(vm.runInContext('memory({buffer: shared}) && heap({buffer: shared})', context), true);
});

test('unknown generated checks fail before patching', () => {
  assert.throws(() => patchSource(fixture + '\nother instanceof SharedArrayBuffer;'), /expected 5/);
  assert.throws(() => patchSource(fixture.replace('wasmMemory.buffer', 'unknown')), /expected 5/);
});

test('export patch is idempotent, restores exact bytes, and rejects later edits', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'redblue-sab-test-'));
  try {
    fs.writeFileSync(path.join(root, 'profiler-build.json'), JSON.stringify({threads: true, buildId: 'test'}));
    for (const file of files) {
      fs.mkdirSync(path.dirname(path.join(root, file)), {recursive: true});
      fs.writeFileSync(path.join(root, file), fixture);
    }
    assert.equal(apply(root).files.reduce((n, f) => n + f.replacements, 0), 15);
    assert.equal(apply(root).status, 'applied');
    assert.equal(apply(root, true).status, 'restored');
    for (const file of files) assert.equal(fs.readFileSync(path.join(root, file), 'utf8'), fixture);
    apply(root);
    fs.appendFileSync(path.join(root, files[1]), '// later change');
    assert.throws(() => apply(root, true), /refusing to overwrite/);
    assert.match(fs.readFileSync(path.join(root, files[0]), 'utf8'), /constructor\.name/);
  } finally {
    fs.rmSync(root, {recursive: true, force: true});
  }
});

test('V5 observer reports capability and preserves original outcomes', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'redblue-diagnostics-test-'));
  try {
    fs.mkdirSync(path.join(root, 'minigame'), {recursive: true});
    fs.mkdirSync(path.join(root, 'profiler-backups'), {recursive: true});
    fs.writeFileSync(path.join(root, 'profiler-build.json'), JSON.stringify({buildId: 'test'}));
    fs.writeFileSync(path.join(root, 'profiler-session.json'), JSON.stringify({resourceUrl: 'http://test'}));
    fs.writeFileSync(path.join(root, 'minigame/webgl.wasm.framework.unityweb.js'), 'GameGlobal.unityNamespace.UnityModule = original;');
    fs.writeFileSync(path.join(root, 'minigame/game.js'), '/* REDBLUE_STARTUP_PHASES_V2 REDBLUE_PLUGIN_IMPORT_DIAGNOSTICS_V1 */');
    execFileSync(process.execPath, [path.join(__dirname, 'Add-StartupDiagnostics.cjs'), root]);
    const source = fs.readFileSync(path.join(root, 'minigame/webgl.wasm.framework.unityweb.js'), 'utf8');
    const error = new Error('original error');
    for (const support of [true, false, undefined]) {
    for (const mode of ['sync-error', 'async-error', 'ready', 'plain']) {
      const records = [];
      const original = () => {
        if (mode === 'sync-error') throw error;
        if (mode === 'async-error') return Promise.reject(error);
        return mode === 'ready' ? Promise.resolve(42) : 42;
      };
      const context = vm.createContext({original, GameGlobal: {unityNamespace: {}},
        wx: {env: {isSupportStandardWorker: support}, getSystemInfoSync: () => ({platform: 'android', version: '8.0.74', SDKVersion: '3.17.4'}),
          request: req => records.push(req.data)},
        console: {info() {}}, setTimeout, clearTimeout, WebAssembly});
      vm.runInContext(source, context);
      if (mode === 'sync-error') assert.throws(() => context.GameGlobal.unityNamespace.UnityModule({}), e => e === error);
      else if (mode === 'async-error') await assert.rejects(context.GameGlobal.unityNamespace.UnityModule({}), e => e === error);
      else assert.equal(await context.GameGlobal.unityNamespace.UnityModule({}), 42);
      assert.equal(records[0].standardWorkerSupport, support === undefined ? null : support);
      assert.equal(records[0].wechatVersion, '8.0.74');
      assert.equal(records[0].diagnosticVersion, 5);
    }
    }
  } finally {
    fs.rmSync(root, {recursive: true, force: true});
  }
});
