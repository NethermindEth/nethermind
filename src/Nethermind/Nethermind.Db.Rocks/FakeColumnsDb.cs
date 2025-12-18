// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Prometheus;

namespace Nethermind.Db.Rocks;

public class FakeColumnsDb<T>(
    Dictionary<T, IDb> innerDb
): IColumnsDb<T> where T : notnull
{
    public void Flush(bool onlyWal = false)
    {
        foreach (var keyValuePair in innerDb)
        {
            keyValuePair.Value.Flush(onlyWal);
        }
    }

    public void Dispose()
    {
    }

    public IDb GetColumnDb(T key)
    {
        return innerDb[key];
    }

    public IEnumerable<T> ColumnKeys => innerDb.Keys;
    public IColumnsWriteBatch<T> StartWriteBatch()
    {
        return new FakeWriteBatch(innerDb);
    }

    public IColumnDbSnapshot<T> CreateSnapshot()
    {
        return new FakeSnapshot(innerDb.ToDictionary((kv) => kv.Key, (kv) =>
        {
            return ((ISnapshottableKeyValueStore)kv.Value).CreateSnapshot();
        }));
    }

    private class FakeWriteBatch : IColumnsWriteBatch<T>
    {
        private Dictionary<T, IWriteBatch> _innerWriteBatch;

        public FakeWriteBatch(Dictionary<T, IDb> innerDb)
        {
            _innerWriteBatch = innerDb
                .ToDictionary((kv) => kv.Key, (kv) => kv.Value.StartWriteBatch());
        }

        public IWriteBatch GetColumnBatch(T key)
        {
            return _innerWriteBatch[key];
        }

        private static Histogram _rocksdBPersistenceTimes = DevMetric.Factory.CreateHistogram("fake_column_dispose_time", "aha", new HistogramConfiguration()
        {
            LabelNames = new[] { "type" },
            // Buckets = Histogram.PowersOfTenDividedBuckets(2, 12, 5)
            Buckets = [1]
        });

        public void Dispose()
        {
            using ArrayPoolList<Task> disposeTasks = new ArrayPoolList<Task>(_innerWriteBatch.Count);
            foreach (var keyValuePair2 in _innerWriteBatch)
            {
                var keyValuePair = keyValuePair2;
                disposeTasks.Add(Task.Run(() =>
                {
                    long sw = Stopwatch.GetTimestamp();
                    keyValuePair.Value.Dispose();
                    _rocksdBPersistenceTimes.WithLabels(keyValuePair.Key.ToString()!).Observe(Stopwatch.GetTimestamp() - sw);
                }));
            }

            Task.WaitAll(disposeTasks);
        }
    }

    private class FakeSnapshot(Dictionary<T, IDbSnapshot> innerDb) : IColumnDbSnapshot<T>
    {

        public IReadOnlyKeyValueStore GetColumn(T key)
        {
            return innerDb[key];
        }

        public void Dispose()
        {
            foreach (var keyValuePair in innerDb)
            {
                keyValuePair.Value.Dispose();
            }
        }
    }
}
