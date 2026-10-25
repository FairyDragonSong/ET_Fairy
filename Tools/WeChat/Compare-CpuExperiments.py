"""Summarize complete fixed-tick phone benchmark sessions, preserving missing-data gaps."""
import argparse
import json
from pathlib import Path
import statistics


METRICS = ['meanStepMs', 'p95StepMs', 'maxStepMs', 'gridMs', 'separationMs', 'decisionMs', 'commitMs']


def load_sessions(folder):
    sessions = {}
    for path in sorted(Path(folder).glob('*.json')):
        record = json.loads(path.read_text(encoding='utf-8-sig'))
        for item in record if isinstance(record, list) else [record]:
            if not isinstance(item, dict):
                continue
            result = item.get('result', item)
            if not isinstance(result, dict) or result.get('schema') != 1 or not all(
                    f in result for f in ['buildId', 'sessionId', 'requestedExecution', 'run']):
                continue
            key = (result['buildId'], result['sessionId'], result['formation'], result['requestedExecution'])
            # A repeated upload replaces only its own run, never duplicates its weight.
            sessions.setdefault(key, {})[result['run']] = result
    return sessions


def summarize(folder):
    output = []
    sessions = load_sessions(folder)
    for (build, session, formation, execution), by_run in sessions.items():
        runs = list(by_run.values())
        sample = runs[0]
        complete = set(by_run) == {0, 1, 2}
        comparable = all(r['count'] == 50000 and r['formation'] in range(4) and r['seed'] == 7241
                         and r['warmupTicks'] == 300 and r['measuredTicks'] == 300 and r['burstVerified'] for r in runs)
        output.append({
            'buildId': build, 'sessionId': session, 'formation': formation, 'variant': sample['variant'], 'execution': execution,
            'runs': len(runs), 'complete': complete, 'validFixedTickBurst': comparable,
            'device': sample['device'], 'gpu': sample['gpu'], 'resolution': [sample['screenWidth'], sample['screenHeight']],
            'profilerStates': sorted(set(r['profilerEnabled'] for r in runs)),
            'stateReplayConsistent': len({r['stateHash'] for r in runs}) == 1,
            'stateHash': sample['stateHash'], 'separationWorkersObserved': any(r.get('separationWorkersObserved', False) for r in runs),
            'separationThreadIndices': sorted({i for r in runs for i in r.get('separationThreadIndices', [])}),
            'jobWorkers': sample['jobWorkers'],
            'development': sample.get('development'),
            'median': {field: statistics.median(r[field] for r in runs) for field in
                       METRICS}
        })
    return output


def compare_packed(folder):
    sessions = load_sessions(folder)
    output = []
    context_fields = ['device', 'gpu', 'unity', 'screenWidth', 'screenHeight',
                      'simdBuild', 'threadsBuild', 'profilerEnabled', 'jobWorkers', 'development']
    paired_sessions = {}
    for (build, session, formation, execution), runs in sessions.items():
        paired_sessions.setdefault((build, session, formation), {})[execution] = runs
    for (build, session, formation), modes in paired_sessions.items():
        bounded = 'BurstPackedBoundedSingleThread' in modes or any(
            r['variant'] == 'packed-bounded' for runs in modes.values() for r in runs.values())
        split = 'BurstPackedSplitSingleThread' in modes or any(
            r['variant'] == 'packed-split' for runs in modes.values() for r in runs.values())
        candidate_name = 'BurstPackedSplitSingleThread' if split else 'BurstPackedBoundedSingleThread' if bounded else 'BurstPackedSingleThread'
        baseline_name = 'BurstPackedSingleThread' if bounded or split else 'BurstSingleThread'
        if not bounded and candidate_name not in modes:
            continue
        candidate = modes.get(candidate_name, {})
        baseline = modes.get(baseline_name, {})
        rows = list(baseline.values()) + list(candidate.values())
        complete = set(baseline) == set(candidate) == {0, 1, 2}
        same_context = all(r.get(f) == rows[0].get(f) for r in rows for f in context_fields)
        fixed = all(r['count'] == 50000 and r['formation'] in range(4) and r['seed'] == 7241
                    and r['warmupTicks'] == r['measuredTicks'] == 300 and r['burstVerified']
                    and not r['profilerEnabled'] and not r['threadsBuild'] and not r['simdBuild']
                    and not r.get('separationWorkersObserved', False) for r in rows)
        hashes_match = len({r['stateHash'] for r in rows}) == 1
        comparison = {'buildId': build, 'sessionId': session, 'formation': formation, 'complete': complete,
                      'baselineExecution': baseline_name, 'candidateExecution': candidate_name,
                      'sameContext': same_context, 'validSingleThreadConditions': fixed,
                      'stateHashesMatch': hashes_match,
                      'validComparison': complete and same_context and fixed and hashes_match}
        if complete:
            comparison['baselineMedian'] = {f: statistics.median(r[f] for r in baseline.values()) for f in METRICS}
            comparison['packedMedian'] = {f: statistics.median(r[f] for r in candidate.values()) for f in METRICS}
            comparison['candidateMedian'] = comparison['packedMedian']
            comparison['medianChangePercent'] = {
                f: (comparison['packedMedian'][f] / comparison['baselineMedian'][f] - 1) * 100
                if comparison['baselineMedian'][f] else None for f in METRICS}
            comparison['pairedStepChangePercent'] = [
                (candidate[i]['meanStepMs'] / baseline[i]['meanStepMs'] - 1) * 100 for i in range(3)]
        output.append(comparison)
    return output


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('folder', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    rows = summarize(args.folder)
    report = {'schema': 1, 'sessions': rows, 'packedComparisons': compare_packed(args.folder),
              'note': 'Only complete 3-run fixed-tick sessions are acceptance data. Match phone, profiler and thermal conditions. '
                      'For pure parallel scaling, compare BurstColoredSingleThread with BurstParallel and verify stateHash equality; '
                      'the original BurstSingleThread uses a different pair order. '
                      'Packed comparisons use the same build/session and original pair order; negative change means faster. '
                      'Total Step includes gather/scatter. A valid comparison alone does not establish a stable gain.'}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    for r in rows:
        print(f"{r['variant']} / formation={r['formation']} / {r['execution']}: {r['runs']}/3 runs, "
              f"Step {r['median']['meanStepMs']:.3f} ms, Separate {r['median']['separationMs']:.3f} ms, "
              f"actual workers={r['separationWorkersObserved']}, complete={r['complete']}")
    if not rows:
        print('No uploaded phone CPU benchmark results yet.')
