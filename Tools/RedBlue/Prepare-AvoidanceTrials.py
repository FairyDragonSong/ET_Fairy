"""Generate temporary Editor-only Burst experiments; production kernel is untouched."""
from pathlib import Path

root = Path(__file__).resolve().parents[2]
source = (root / 'Unity/Assets/Scripts/Loader/RedBlue/BurstCrowdKernel.cs').read_text(encoding='utf-8-sig')
source = source.replace('BurstCrowdKernel', 'AvoidanceTrialKernel')
source = source.replace('private readonly List<GCHandle>', 'public static int PairLimit = 48, Interval = 1, Approximate;\n        private readonly List<GCHandle>', 1)
source = source.replace('Count = s.Count, Radius', 'PairLimit = PairLimit, Interval = Interval, Approximate = Approximate,\n                    Count = s.Count, Radius', 1)
source = source.replace('if (parallel)', 'if (buffers.Interval != 1 && (tick - 1) % buffers.Interval != 0) { }\n            else if (parallel)', 1)
source = source.replace('public int Count, Tick, ZeroDamage,', 'public int PairLimit, Interval, Approximate;\n            public int Count, Tick, ZeroDamage,', 1)
clear = 'UnsafeUtility.MemClear(forceX, Count * sizeof(float)); UnsafeUtility.MemClear(forceY, Count * sizeof(float));'
assert source.count(clear) == 1
source = source.replace(clear, 'if (Interval == 1 || (Tick - 1) % Interval == 0) { ' + clear + ' }')
source = source.replace('>= 48', '>= PairLimit').replace('< 48', '< PairLimit')
force = 'float f = diameter / math.sqrt(d2) - 1;'
assert source.count(force) == 1
source = source.replace(force, '''float f;
                    if (Approximate == 2 && d2 >= diameter2 * .01f)
                        f = 2 * (diameter2 - d2) / (diameter2 + 3 * d2);
                    else if (Approximate == 1 && d2 >= diameter2 * .64f)
                    {
                        float t = 1 - d2 / diameter2;
                        f = t * (.5f + t * (.375f + t * .3125f));
                    }
                    else f = diameter / math.sqrt(d2) - 1;''')
runner = (root / 'Tools/RedBlue/AvoidanceTrialsRunner.cs.txt').read_text(encoding='utf-8')
source = source.replace(': IDisposable', ': IAvoidanceTrial')
noalias = source.replace('AvoidanceTrialKernel', 'NoAliasTrialKernel').replace('[NativeDisableUnsafePtrRestriction]', '[NoAlias, NativeDisableUnsafePtrRestriction]')
# Second source shares the same imports; only append its namespace/class body.
noalias = noalias[noalias.index('namespace ET.RedBlue'):]
packed = source.replace('AvoidanceTrialKernel', 'PackedTrialKernel')
packed = packed.replace('Compiled = (int*)Pin(new int[2])', 'packedX = (float*)Pin(new float[s.Count]), packedY = (float*)Pin(new float[s.Count]),\n                    Compiled = (int*)Pin(new int[2])')
packed = packed.replace('[NativeDisableUnsafePtrRestriction] public int* Compiled;', '[NativeDisableUnsafePtrRestriction] public int* Compiled;\n            [NativeDisableUnsafePtrRestriction] public float* packedX;\n            [NativeDisableUnsafePtrRestriction] public float* packedY;')
placement = 'ids[cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++] = i;'
assert packed.count(placement) == 1
packed = packed.replace(placement, '{ int slot = cursors[(int)(Y[i] / diameter) * sepCols + (int)(X[i] / diameter)]++; ids[slot] = i; packedX[slot] = X[i]; packedY[slot] = Y[i]; }')
packed = packed.replace('float xi = X[i], yi = Y[i]', 'float xi = packedX[p], yi = packedY[p]')
packed = packed.replace('float dx = xi - X[j], dy = yi - Y[j]', 'float dx = xi - packedX[q], dy = yi - packedY[q]')
packed = packed[packed.index('namespace ET.RedBlue'):]
destination = root / 'Unity/Assets/Scripts/Loader/RedBlue/AvoidanceTrials.EditorOnly.cs'
if destination.exists():
    raise FileExistsError('Remove only the previously generated experiment before regenerating')
destination.write_text('#if UNITY_EDITOR\n' + source + '\n' + noalias + '\n' + packed + '\n' + runner + '\n#endif\n', encoding='utf-8')
print(destination)
