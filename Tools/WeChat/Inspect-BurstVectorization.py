"""Count real Wasm SIMD opcodes in archived Burst objects and map job entry points."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('evidence', type=Path)
args = parser.parse_args()
text = (args.evidence / 'burst-disassembly.txt').read_text(encoding='utf-8-sig', errors='replace')
methods = (args.evidence / 'lib_burst_generated.txt').read_text(encoding='utf-8-sig', errors='replace')
entry_points = {}
for line in methods.splitlines():
    match = re.search(r'--([a-f0-9]{32})$', line)
    if match:
        entry_points[match[1]] = line
sections = re.split(r'^.*?\(lib_burst_generated_part_([a-f0-9]{32})\.o\):\s+file format wasm\s*$', text, flags=re.MULTILINE)
results = []
for i in range(1, len(sections), 2):
    ident, body = sections[i:i+2]
    name = entry_points.get(ident, 'unmapped')
    if 'ET.RedBlue.' not in name:
        continue
    opcodes = Counter(re.findall(r'\b(?:v128|f32x4|f64x2|i8x16|i16x8|i32x4|i64x2)\.[a-zA-Z0-9_]+', body))
    results.append({'entryHash': ident, 'method': name, 'vectorOpcodeCount': sum(opcodes.values()), 'opcodes': dict(opcodes)})
report = {'schema': 1, 'jobs': results,
          'note': 'Static opcode counts, not executed instruction counts. A StageJob spans multiple stages; SIMD here does not prove its Separation hot loop is vectorized.'}
headers = list(re.finditer(r'^[0-9a-fA-F]+ <(.+)>:$', text, flags=re.MULTILINE))
shared = []
for i, header in enumerate(headers):
    name = header.group(1)
    if 'ET.RedBlue.BurstCrowdKernel' not in name:
        continue
    body = text[header.end():headers[i+1].start() if i+1 < len(headers) else len(text)]
    opcodes = Counter(re.findall(r'\b(?:v128|f32x4|f64x2|i8x16|i16x8|i32x4|i64x2)\.[a-zA-Z0-9_]+', body))
    shared.append({'symbol': name, 'vectorOpcodeCount': sum(opcodes.values()), 'opcodes': dict(opcodes)})
report['outlinedKernelFunctions'] = shared
report['archiveVectorOpcodeCount'] = len(re.findall(r'\b(?:v128|f32x4|f64x2|i8x16|i16x8|i32x4|i64x2)\.[a-zA-Z0-9_]+', text))
report['note'] += ' Outlined helpers may hold the actual math even when job wrappers have no SIMD. Archive-wide counts alone do not establish reachability.'
(args.evidence / 'burst-vectorization.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
for result in results:
    print(result['entryHash'], 'SIMD opcodes:', result['vectorOpcodeCount'])
for function in shared:
    print('Outlined kernel SIMD opcodes:', function['vectorOpcodeCount'], function['symbol'][:120])
if not results:
    raise SystemExit('No mapped RedBlue Burst jobs found; inspect archived disassembly/method list')
