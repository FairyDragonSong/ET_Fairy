"""Keep fixed-tick comparisons independent across formations and modes."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('cpu', Path(__file__).with_name('Compare-CpuExperiments.py'))
cpu = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cpu)


class FormationTests(unittest.TestCase):
    def fixture(self):
        rows = []
        for formation in range(4):
            for execution in ('BurstPackedSingleThread', 'BurstPackedSplitSingleThread'):
                for run in range(3):
                    row = dict(schema=1, buildId='test', sessionId='test', variant='packed-split-all',
                               formation=formation, requestedExecution=execution, run=run,
                               count=50000, seed=7241, warmupTicks=300, measuredTicks=300,
                               burstVerified=True, device='test', gpu='test', unity='test',
                               screenWidth=1280, screenHeight=720, simdBuild=False, threadsBuild=False,
                               profilerEnabled=False, jobWorkers=0, development=False,
                               separationWorkersObserved=False, separationThreadIndices=[0],
                               stateHash=f'formation-{formation}')
                    row.update({metric: 10 for metric in cpu.METRICS})
                    rows.append(row)
        return rows

    def compare(self, rows):
        with tempfile.TemporaryDirectory(prefix='redblue-cpu-formations-') as directory:
            for i, row in enumerate(rows):
                Path(directory, f'{i:02}.json').write_text(json.dumps({'result': row}), encoding='utf-8')
            return cpu.summarize(directory), cpu.compare_packed(directory)

    def test_four_independent_hashes_and_run_groups(self):
        sessions, comparisons = self.compare(self.fixture())
        self.assertEqual(len(sessions), 8)
        self.assertEqual(len(comparisons), 4)
        self.assertTrue(all(r['validComparison'] for r in comparisons))

    def test_missing_run_cannot_be_filled_by_another_formation(self):
        rows = self.fixture()[:-1]
        _, comparisons = self.compare(rows)
        by_formation = {r['formation']: r for r in comparisons}
        self.assertFalse(by_formation[3]['validComparison'])
        self.assertTrue(all(by_formation[i]['validComparison'] for i in range(3)))

    def test_hash_mismatch_fails_only_its_formation(self):
        rows = self.fixture()
        rows[-1]['stateHash'] = 'different'
        _, comparisons = self.compare(rows)
        self.assertEqual(sum(r['validComparison'] for r in comparisons), 3)


if __name__ == '__main__':
    unittest.main()
