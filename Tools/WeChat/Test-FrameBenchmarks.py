"""Check the FPS acceptance gate against incomplete or misleading measurements."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('frames', Path(__file__).with_name('Analyze-FrameBenchmarks.py'))
frames = importlib.util.module_from_spec(spec)
spec.loader.exec_module(frames)


class AcceptanceTests(unittest.TestCase):
    def fixture(self):
        return [dict(kind='frame', buildId='test', sessionId='test', scenario='full-packed',
                     window=i, width=1280, height=720, gpu='test', backend='Burst packed / single thread',
                     renderer='State texture / static mesh', development=False, count=50000, formation=1, seed=7241,
                     focusedThroughout=True, profilerEnabled=False, targetFps=60, seconds=5, frames=300,
                     burstVerified=True, alive=50000, drawn=50000, zeroDamage=True,
                     fps=60, simulationHz=30, steps=150, p95FrameMs=17, maxFrameMs=18) for i in range(6)]

    def reports(self, rows):
        with tempfile.TemporaryDirectory(prefix='redblue-frame-gate-') as directory:
            for i, row in enumerate(rows):
                Path(directory, f'{i}.json').write_text(json.dumps({'peer': 'test-fixture', 'result': row}), encoding='utf-8')
            return frames.summarize(directory)['scenarios']

    def report(self, rows):
        return self.reports(rows)[0]

    def test_formations_are_independent_acceptance_groups(self):
        rows = []
        for formation in range(4):
            rows.extend(dict(row, formation=formation) for row in self.fixture())
        reports = self.reports(rows)
        self.assertEqual({r['formation'] for r in reports}, set(range(4)))
        self.assertTrue(all(r['fullBattle58FpsPassed'] for r in reports))
        rows[-1].update(fps=50, frames=250)
        reports = {r['formation']: r for r in self.reports(rows)}
        self.assertFalse(reports[3]['fullBattle58FpsPassed'])
        self.assertTrue(all(reports[i]['fullBattle58FpsPassed'] for i in range(3)))

    def test_partial_formations_cannot_complete_each_other(self):
        rows = self.fixture()
        for row in rows[3:]:
            row['formation'] = 0
        self.assertTrue(all(not r['validWindows'] for r in self.reports(rows)))

    def test_unknown_formation_is_invalid(self):
        rows = [dict(row, formation=4) for row in self.fixture()]
        self.assertFalse(self.report(rows)['validWindows'])

    def test_full_scene_at_realtime_passes(self):
        self.assertTrue(self.report(self.fixture())['fullBattle55FpsPassed'])

    def test_one_slow_window_cannot_hide_in_average(self):
        rows = self.fixture()
        rows[0].update(fps=50, frames=250)
        self.assertFalse(self.report(rows)['fullBattle55FpsPassed'])

    def test_incomplete_and_duplicate_windows_fail(self):
        rows = self.fixture()
        self.assertFalse(self.report(rows[:-1])['fullBattle55FpsPassed'])
        rows[-1]['window'] = 0
        self.assertFalse(self.report(rows)['fullBattle55FpsPassed'])

    def test_reduced_population_tick_rate_or_hidden_fails(self):
        for field, value in [('alive', 49999), ('drawn', 49999), ('count', 20000),
                             ('simulationHz', 15), ('focusedThroughout', False), ('profilerEnabled', True)]:
            with self.subTest(field=field):
                rows = self.fixture()
                rows[2][field] = value
                self.assertFalse(self.report(rows)['fullBattle55FpsPassed'])

    def test_draw_only_never_establishes_full_battle_goal(self):
        rows = self.fixture()
        for row in rows:
            row.update(scenario='draw-only', simulationHz=0, steps=0, burstVerified=False)
        self.assertTrue(self.report(rows)['validWindows'])
        self.assertFalse(self.report(rows)['fullBattle55FpsPassed'])

    def test_logic_only_requires_realtime_and_all_50000_alive(self):
        rows = self.fixture()
        for row in rows:
            row.update(scenario='sim-packed', drawn=0, activeCameras=0)
        self.assertTrue(self.report(rows)['logicOnly55FpsPassed'])
        self.assertFalse(self.report(rows)['fullBattle55FpsPassed'])
        rows[0]['activeCameras'] = 1
        self.assertFalse(self.report(rows)['logicOnly55FpsPassed'])
        rows[0]['activeCameras'] = 0
        rows[0]['simulationHz'] = 15
        self.assertFalse(self.report(rows)['logicOnly55FpsPassed'])
        rows[0]['simulationHz'] = 30
        rows[1]['alive'] = 25000
        self.assertFalse(self.report(rows)['logicOnly55FpsPassed'])

    def test_58_gate_requires_every_window_and_realtime(self):
        rows = self.fixture()
        for row in rows:
            row.update(scenario='sim-split-packed', drawn=0, activeCameras=0, splitTicks=True)
        self.assertTrue(self.report(rows)['logicOnly58FpsPassed'])
        rows[2].update(fps=57.8, frames=289)
        self.assertTrue(self.report(rows)['logicOnly55FpsPassed'])
        self.assertFalse(self.report(rows)['logicOnly58FpsPassed'])
        rows[2].update(fps=60, frames=300, simulationHz=15)
        self.assertFalse(self.report(rows)['logicOnly58FpsPassed'])


if __name__ == '__main__':
    unittest.main()
