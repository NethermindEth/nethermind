"""Generate benchmark baselines without modifying production sources.

Run from any directory:
  python tools/CacheExperiments/prepare.py [baseline-revision]
  dotnet run --project tools/CacheExperiments -c Release -- bdn
  dotnet run --project tools/CacheExperiments -c Release -- results.csv

Set DOTNET_TieredCompilation=0 for the controlled, fully optimized JIT comparison.
The default revision is the source inspected before the cache experiments.
"""

from pathlib import Path
import subprocess
import sys

root = Path(__file__).resolve().parents[2]
revision = sys.argv[1] if len(sys.argv) > 1 else '487bddc799823fb998f1793fe4717cf73faea109'
output = root / 'artifacts/cache-experiments/generated'
output.mkdir(parents=True, exist_ok=True)

for name in ['LruCache', 'LruKeyCache', 'LinkedListNode', 'ClockCache', 'ClockCacheBase']:
    source = subprocess.check_output(
        ['git', 'show', f'{revision}:src/Nethermind/Nethermind.Core/Caching/{name}.cs'],
        cwd=root, text=True,
    )
    source = source.replace('namespace Nethermind.Core.Caching', 'namespace CacheExperiments.Baseline')
    if name == 'LruCache':
        source = source.replace('using System;', 'using System;\nusing Nethermind.Core;\nusing Nethermind.Core.Caching;')
    elif name == 'ClockCache':
        source = source.replace('using System;', 'using System;\nusing Nethermind.Core;')
    source = source.replace('Collections.CollectionExtensions.LockPartitions', 'Nethermind.Core.Collections.CollectionExtensions.LockPartitions')
    (output / f'{name}.cs').write_text(source, encoding='utf-8')

    if name in ['LruCache', 'LruKeyCache', 'LinkedListNode']:
        fast = source.replace('CacheExperiments.Baseline', 'CacheExperiments.FastPath')
        if name == 'LinkedListNode':
            fast = fast.replace(
                '        if (node.Next == node)\n',
                '        if (ReferenceEquals(leastRecentlyUsed?.Prev, node)) return;\n\n        if (node.Next == node)\n', 1,
            )
        (output / f'Fast{name}.cs').write_text(fast, encoding='utf-8')

source = (output / 'ClockCache.cs').read_text(encoding='utf-8')
source = source.replace('namespace CacheExperiments.Baseline', 'namespace CacheExperiments.Bounded')
source = source.replace(': ClockCacheBase<TKey>', ': Baseline.ClockCacheBase<TKey>')
source = source.replace('int maxCapacity, int? lockPartition', 'int maxCapacity, int scanLimit, int? lockPartition')
source = source.replace('        int position = Clock;', '        int position = Clock;\n        int remaining = Math.Clamp(scanLimit, 1, MaxCapacity);')
source = source.replace('            if (!accessed)', '            if (!accessed || --remaining == 0)')
(output / 'BoundedClockCache.cs').write_text(source, encoding='utf-8')
print(f'Generated baselines from {revision} in {output}')
