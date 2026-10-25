"""Verify a Development export's configured resource HTTP content against local SHA-256."""
import argparse
import hashlib
import json
import re
import urllib.parse
import urllib.request
from pathlib import Path


def verify(root, allow_release=False):
    root = Path(root).resolve()
    manifest = json.loads((root / "profiler-build.json").read_text(encoding="utf-8-sig"))
    if not allow_release and not (manifest["development"] and manifest["autoConnect"]):
        raise ValueError("Expected Development + Autoconnect build")
    game = (root / "minigame/game.js").read_text(encoding="utf-8")
    base = re.search(r"DATA_CDN:\s*'([^']+)'", game).group(1).rstrip("/")
    namespace = (root / 'minigame/unity-namespace.js').read_text(encoding='utf-8')
    prefix_match = re.search(r"streamingUrlPrefixPath:\s*'([^']*)'", namespace)
    prefix = prefix_match.group(1) if prefix_match else ''
    if prefix and prefix != '/__redblue__/' + manifest['buildId']:
        raise ValueError('StreamingAssets prefix differs from Build ID')
    data_id = re.search(r"DATA_FILE_MD5:\s*'([^']+)'", game).group(1)
    code_id = re.search(r"CODE_FILE_MD5:\s*'([^']+)'", game).group(1)
    web = root / "webgl"
    data = list(web.glob(data_id + ".*.bin.txt"))
    code = list((root / "minigame/wasmcode").glob(code_id + ".*.wasm.br"))
    if len(data) != 1 or len(code) != 1:
        raise ValueError("game.js data/code identifiers do not match exported files")
    files = sorted(p for p in (web / "StreamingAssets").rglob("*") if p.is_file() and p.suffix != ".meta")
    files.append(data[0])
    version = (web / "StreamingAssets/Bundles/DefaultPackage/PackageManifest_DefaultPackage.version").read_text().strip()
    if version != manifest["resourceVersion"]:
        raise ValueError("StreamingAssets version differs from build manifest")
    results = []
    for path in files:
        relative = path.relative_to(web).as_posix()
        url = base + (prefix if relative.startswith('StreamingAssets/') else '') + "/" + urllib.parse.quote(relative)
        expected = hashlib.sha256(path.read_bytes()).hexdigest()
        with urllib.request.urlopen(url, timeout=30) as response:
            actual = hashlib.sha256(response.read()).hexdigest()
        if actual != expected:
            raise ValueError("HTTP content mismatch: " + relative)
        results.append({"path": relative, "bytes": path.stat().st_size, "sha256": actual})
    return {"schema": 1, "buildId": manifest["buildId"], "resourceVersion": version,
            "resourceUrl": base, "resourcePrefix": prefix, "httpFilesVerified": len(results), "files": results,
            "wasmCode": {"path": str(code[0]), "bytes": code[0].stat().st_size,
                         "sha256": hashlib.sha256(code[0].read_bytes()).hexdigest()},
            "phoneVerified": False, "note": "Local HTTP integrity check; phone launch and remote Profiler frames are still required."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export_root", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--allow-release", action="store_true")
    args = parser.parse_args()
    result = verify(args.export_root, args.allow_release)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Verified {result['httpFilesVerified']} HTTP files; build={result['buildId']}; phone verification pending")
