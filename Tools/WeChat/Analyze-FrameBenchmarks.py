"""Summarize normal gameplay windows, retaining incomplete/failed runs as evidence."""
import argparse
import json
from pathlib import Path


def summarize(folder, build_id=None, session_id=None):
    groups = {}
    for path in sorted(Path(folder).glob('*.json')):
        record = json.loads(path.read_text(encoding='utf-8-sig'))
        r = record['result']
        if r.get('kind') != 'frame':
            continue
        if build_id is not None and r['buildId'] != build_id:
            continue
        if session_id is not None and r['sessionId'] != session_id:
            continue
        key = (r['buildId'], r['sessionId'], r['formation'], r['scenario'])
        groups.setdefault(key, []).append((path.name, record.get('peer'), r))
    results = []
    for (build, session, formation, scenario), records in groups.items():
        rows = [r for _, _, r in records]
        complete = len(rows) == 6 and sorted(r['window'] for r in rows) == list(range(6))
        seconds = sum(r['seconds'] for r in rows)
        same_context = len({(r['width'], r['height'], r['gpu'], r['backend'], r['renderer'], r['development']) for r in rows}) == 1
        valid = complete and same_context and all(
            r['count'] == 50000 and r['formation'] in range(4) and r['seed'] == 7241
            and r['focusedThroughout'] and not r['profilerEnabled'] and r['targetFps'] == 60
            and r['seconds'] >= 5 and r['frames'] > 0
            and (scenario == 'draw-only' or r['burstVerified']) for r in rows)
        sustained = scenario.startswith('full-')
        population_held = all(r['alive'] == 50000 and r['drawn'] == 50000 and r['zeroDamage'] for r in rows)
        full_battle = scenario.startswith('full-') or scenario.startswith('battle-')
        logic_only = scenario.startswith('sim-')
        realtime = all(29 <= r['simulationHz'] <= 31 for r in rows) if scenario != 'draw-only' else None
        minimum_fps = min(r['fps'] for r in rows)
        results.append({
            'buildId': build, 'sessionId': session, 'formation': formation, 'scenario': scenario,
            'complete': complete, 'validWindows': valid, 'sameContext': same_context,
            'seconds': seconds, 'fps': sum(r['frames'] for r in rows) / seconds,
            'minimumWindowFps': minimum_fps, 'worstWindowP95Ms': max(r['p95FrameMs'] for r in rows),
            'worstFrameMs': max(r['maxFrameMs'] for r in rows),
            'simulationHz': sum(r['steps'] for r in rows) / seconds,
            'realtimeSimulation': realtime, 'sustained50000Visible': population_held if sustained else None,
            'fullBattle55FpsPassed': valid and full_battle and minimum_fps >= 55 and realtime
                and (not sustained or population_held),
            'logicOnly55FpsPassed': valid and logic_only and minimum_fps >= 55 and realtime
                and all(r['alive'] == 50000 and r['drawn'] == 0 and r['zeroDamage']
                        and r.get('activeCameras', -1) == 0 for r in rows),
            'logicOnly58FpsPassed': valid and logic_only and minimum_fps >= 58 and realtime
                and all(r['alive'] == 50000 and r['drawn'] == 0 and r['zeroDamage']
                        and r.get('activeCameras', -1) == 0 for r in rows),
            'fullBattle58FpsPassed': valid and full_battle and minimum_fps >= 58 and realtime
                and (not sustained or population_held),
            'context': {k: rows[0][k] for k in ('width', 'height', 'gpu', 'backend', 'renderer', 'development')},
            'windows': [{'file': name, 'peer': peer, **r} for name, peer, r in records]
        })
    return {'schema': 1, 'note': 'Five-second normal-frame windows; retain failures. 55/58 FPS gates require every window at the named threshold with ~30Hz simulation. Logic-only requires all 50000 alive, drawn=0 and activeCameras=0. No Unity Profiler capture is implied.', 'scenarios': results}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--build-id')
    parser.add_argument('--session-id')
    args = parser.parse_args()
    report = summarize(args.folder, args.build_id, args.session_id)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    for r in report['scenarios']:
        print(f"{r['buildId']} formation={r['formation']} {r['scenario']}: {r['fps']:.2f} FPS, min window {r['minimumWindowFps']:.2f}, sim {r['simulationHz']:.2f} Hz, valid={r['validWindows']}, full58={r['fullBattle58FpsPassed']}, logic58={r['logicOnly58FpsPassed']}")
