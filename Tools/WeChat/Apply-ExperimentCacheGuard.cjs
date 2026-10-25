// Keep preview builds with the same AppID from reusing mutable StreamingAssets metadata.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const vm = require('node:vm');
const sha = data => crypto.createHash('sha256').update(data).digest('hex');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
const files = ['minigame/unity-namespace.js'];

function patchSource(namespace, buildId) {
    if (!/^[A-Za-z0-9_-]+$/.test(buildId)) throw new Error('Invalid experiment Build ID');
    const prefixTarget = "streamingUrlPrefixPath: '',";
    if (namespace.split(prefixTarget).length !== 2) throw new Error('Unexpected StreamingAssets prefix');
    const cacheTarget = 'unityNamespace.isCacheableFile = function (path) {';
    if (namespace.split(cacheTarget).length !== 2) throw new Error('Unexpected generated cache predicate');
    namespace = namespace.replace(prefixTarget, `streamingUrlPrefixPath: '/__redblue__/${buildId}',`);
    namespace = namespace.replace(cacheTarget, cacheTarget + `
    // These entry points change between exports, unlike content-hashed bundles.
    if (/(?:^|\\/)build_info(?:[?#]|$)|\\.version(?:[?#]|$)/.test(path)) return false;`);
    return namespace;
}

function apply(root, rollback = false, allowRelease = false) {
    root = path.resolve(root);
    const manifest = readJson(path.join(root, 'profiler-build.json'));
    if (!manifest.cpuVariant || (!manifest.development && !allowRelease)) throw new Error('Expected Development CPU experiment or explicit --allow-release');
    const recordPath = path.join(root, 'resource-cache-isolation.json');
    const previous = fs.existsSync(recordPath) ? readJson(recordPath) : null;
    if (previous) {
        if (previous.buildId !== manifest.buildId) throw new Error('Cache guard Build ID changed');
        if (previous.schema !== 1 || JSON.stringify(previous.files.map(f => f.path)) !== JSON.stringify(files))
            throw new Error('Unexpected cache guard record');
        for (const file of previous.files) {
            const expected = previous.status === 'applied' ? file.patchedSha256 : file.originalSha256;
            if (sha(fs.readFileSync(path.join(root, file.path))) !== expected)
                throw new Error('File changed since cache guard: ' + file.path);
        }
        if (previous.status === (rollback ? 'restored' : 'applied')) return previous;
    } else if (rollback) throw new Error('No cache guard to restore');
    const originals = files.map(f => fs.readFileSync(path.join(root, f)));
    const backupRoot = path.join(root, 'profiler-backups/resource-cache-isolation');
    const patched = rollback ? files.map((f, i) => {
        const bytes = fs.readFileSync(path.join(backupRoot, path.basename(f)));
        if (sha(bytes) !== previous.files[i].originalSha256) throw new Error('Cache guard backup changed');
        return bytes.toString('utf8');
    }) : [patchSource(originals[0].toString('utf8'), manifest.buildId)];
    // Generated files are ES modules. Validate after removing their import/export declarations.
    for (const source of patched) new vm.Script(source.replace(/^import .*;\r?$/gm, '').replace(/^export default .*;\r?$/gm, ''));
    if (!rollback) {
        fs.mkdirSync(backupRoot, {recursive: true});
        files.forEach((f, i) => {
            const backup = path.join(backupRoot, path.basename(f));
            if (fs.existsSync(backup) && sha(fs.readFileSync(backup)) !== sha(originals[i])) throw new Error('Backup differs');
        });
        files.forEach((f, i) => {
            const backup = path.join(backupRoot, path.basename(f));
            if (!fs.existsSync(backup)) fs.writeFileSync(backup, originals[i], {flag: 'wx'});
        });
    }
    files.forEach((f, i) => fs.writeFileSync(path.join(root, f), patched[i]));
    const record = rollback ? {...previous, status: 'restored'} : {
        schema: 1, buildId: manifest.buildId, status: 'applied',
        files: files.map((f, i) => ({path: f, originalSha256: sha(originals[i]), patchedSha256: sha(patched[i])})),
        resourcePrefix: '/__redblue__/' + manifest.buildId,
        note: 'SDK StreamingAssets prefix isolates each CPU experiment build; mutable version/build_info metadata not cached. No startup cache deletion.',
    };
    record.updatedUtc = new Date().toISOString();
    fs.writeFileSync(recordPath, JSON.stringify(record, null, 2) + '\n');
    return record;
}

if (require.main === module) {
    if (!process.argv[2] || process.argv.slice(3).some(arg => !['--rollback', '--allow-release'].includes(arg)))
        throw new Error('Usage: node Apply-ExperimentCacheGuard.cjs EXPORT_ROOT [--rollback] [--allow-release]');
    console.log(JSON.stringify(apply(process.argv[2], process.argv.includes('--rollback'), process.argv.includes('--allow-release')), null, 2));
}
module.exports = {patchSource, apply};
