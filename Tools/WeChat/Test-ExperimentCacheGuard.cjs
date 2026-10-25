const {test} = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {patchSource, apply} = require('./Apply-ExperimentCacheGuard.cjs');
const namespace = "const unityNamespace = {streamingUrlPrefixPath: '',};\r\nunityNamespace.isCacheableFile = function (path) { return path.includes('StreamingAssets'); };\r\n";

test('builds use separate SDK resource paths without altering startup or deleting cache', () => {
    const a = patchSource(namespace, 'build-a'), b = patchSource(namespace, 'build-b');
    assert.match(a, /streamingUrlPrefixPath: '\/__redblue__\/build-a'/);
    assert.match(b, /streamingUrlPrefixPath: '\/__redblue__\/build-b'/);
    assert.ok(!a.includes('cleanAllCache'));
    assert.throws(() => patchSource(namespace, '../unsafe'));
    assert.throws(() => patchSource('changed', 'build'));
});

test('mutable entry points bypass cache while hashed bundles retain the existing policy', () => {
    const policy = vm.runInNewContext(patchSource(namespace, 'build') + '; unityNamespace.isCacheableFile');
    assert.equal(policy('https://cdn/__redblue__/build/StreamingAssets/build_info'), false);
    assert.equal(policy('https://cdn/__redblue__/build/StreamingAssets/Bundles/pkg.version?t=1'), false);
    assert.equal(policy('https://cdn/__redblue__/build/StreamingAssets/Bundles/hash.bundle'), true);
});

test('apply is idempotent, rollback is exact, changed exports are protected', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'redblue-cache-test-'));
    try {
        fs.mkdirSync(path.join(root, 'minigame'));
        fs.writeFileSync(path.join(root, 'profiler-build.json'), JSON.stringify({cpuVariant: 'packed-ab', development: true, buildId: 'build'}));
        const file = path.join(root, 'minigame/unity-namespace.js');
        fs.writeFileSync(file, namespace);
        apply(root); assert.equal(apply(root).status, 'applied');
        apply(root, true); assert.equal(fs.readFileSync(file, 'utf8'), namespace);
        apply(root); fs.appendFileSync(file, '// changed');
        assert.throws(() => apply(root, true), /File changed/);
    } finally { fs.rmSync(root, {recursive: true}); }
});
