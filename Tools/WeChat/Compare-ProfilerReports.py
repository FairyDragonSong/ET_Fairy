"""Compare two WeChatProfilerReport report.json files; standard library only."""
import argparse
import json
from pathlib import Path


def compare(baseline, candidate):
    a, b = (json.loads(Path(p).read_text(encoding="utf-8-sig")) for p in (baseline, candidate))
    if a.get("schema") != 1 or b.get("schema") != 1:
        raise ValueError("Expected schema=1 profiler reports")
    lines = ["# Unity Profiler comparison", "",
             f"Baseline: {Path(baseline).resolve()}", f"Candidate: {Path(candidate).resolve()}", "",
             "Compare the same phone, scenario, camera, resolution, warmup and build type. "
             "Profiler CPU frame time includes waits; it is not a standalone release FPS measurement.", ""]
    for title, path in (("Baseline", baseline), ("Candidate", candidate)):
        metadata = Path(path).parent / "capture.json"
        if metadata.exists():
            capture = json.loads(metadata.read_text(encoding="utf-8-sig"))
            lines += [f"{title}: {capture.get('label')}; device={capture.get('device')}; "
                      f"target={capture.get('connection')}; valid={capture.get('valid')}; "
                      f"truncated={capture.get('ringBufferTruncated')}", ""]
            if not capture.get("valid"):
                lines += [f"WARNING: {title} capture is incomplete/invalid; do not use it for acceptance.", ""]
    before = {(t["group"], t["name"]): t for t in a["threads"]}
    for thread in b["threads"]:
        old = before.get((thread["group"], thread["name"]))
        if old is None:
            continue
        lines += [f"## {thread['group']} / {thread['name']}", "",
                  "| Frame ms | Baseline | Candidate | Delta |", "| --- | ---: | ---: | ---: |"]
        for field in ("mean", "p50", "p95", "p99", "max"):
            x, y = old["frameTimeMs"][field], thread["frameTimeMs"][field]
            if x is not None and y is not None:
                lines.append(f"| {field} | {x:.3f} | {y:.3f} | {y-x:+.3f} |")
        markers_a = {m["name"]: m for m in old["markers"]}
        markers_b = {m["name"]: m for m in thread["markers"]}
        deltas = []
        for name in markers_a.keys() | markers_b.keys():
            x = markers_a.get(name, {}).get("selfMsPerFrame", 0)
            y = markers_b.get(name, {}).get("selfMsPerFrame", 0)
            deltas.append((name, x, y))
        deltas.sort(key=lambda r: abs(r[2]-r[1]), reverse=True)
        lines += ["", "| Marker self ms/frame | Baseline | Candidate | Delta |",
                  "| --- | ---: | ---: | ---: |"]
        for name, x, y in deltas[:30]:
            safe_name = name.replace("|", "\\|").replace("\n", " ")
            lines.append(f"| {safe_name} | {x:.3f} | {y:.3f} | {y-x:+.3f} |")
        lines.append("")
    return "\n".join(lines)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline")
    parser.add_argument("candidate")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = compare(args.baseline, args.candidate)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(result, encoding="utf-8")
    else:
        print(result)
