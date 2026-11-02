// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.ProfilingMcp.Memory;
using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class HeapSnapshotTests
{
    [Test]
    public void Diff_reports_grown_new_and_vanished_types_by_absolute_byte_change()
    {
        HeapSnapshot before = Snapshot(new TypeStat("Stable", 10, 100, 0), new TypeStat("Growing", 5, 50, 0), new TypeStat("Gone", 3, 300, 0));
        HeapSnapshot after = Snapshot(new TypeStat("Stable", 10, 100, 0), new TypeStat("Growing", 50, 500, 0), new TypeStat("New", 1, 20, 0));

        IReadOnlyList<TypeDelta> diff = HeapSnapshot.Diff(before, after);

        Assert.That(diff, Is.EqualTo(new[]
        {
            new TypeDelta("Growing", 45, 450, 50, 500),
            new TypeDelta("Gone", -3, -300, 0, 0),
            new TypeDelta("New", 1, 20, 1, 20),
        }));
    }

    [Test]
    public void Round_trips_through_json()
    {
        HeapSnapshot snapshot = Snapshot(new TypeStat("System.Byte[]", 2, 2048, 1024));
        string path = Path.Combine(Path.GetTempPath(), $"heap-{Guid.NewGuid():N}.json");
        try
        {
            snapshot.Save(path);
            HeapSnapshot loaded = HeapSnapshot.Load(path);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(loaded.Types, Is.EqualTo(snapshot.Types));
                Assert.That(loaded.GenerationBytes, Is.EqualTo(snapshot.GenerationBytes));
                Assert.That((loaded.Target, loaded.TotalBytes, loaded.TakenAtUtc), Is.EqualTo((snapshot.Target, snapshot.TotalBytes, snapshot.TakenAtUtc)));
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HeapSnapshot Snapshot(params TypeStat[] types) =>
        new("1234", new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc), types.Sum(t => t.Count), types.Sum(t => t.Bytes), 0,
            new Dictionary<string, long> { ["gen2"] = types.Sum(t => t.Bytes) }, types);
}
