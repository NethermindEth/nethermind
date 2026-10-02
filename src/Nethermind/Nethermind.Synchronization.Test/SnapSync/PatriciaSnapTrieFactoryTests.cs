// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class PatriciaSnapTrieFactoryTests
{
    // Pinned: databases written by earlier versions are read back with this key and value.
    private static readonly byte[] AccountProgressKey = "AccountProgressKey"u8.ToArray();

    [Test]
    public void Records_finished_range_phase_in_the_state_db()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        Assert.That(factory.IsRangePhaseFinished(), Is.False);

        factory.MarkRangePhaseFinished();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateDb[AccountProgressKey], Is.EqualTo(Keccak.MaxValue.BytesToArray()));
            Assert.That(stateDb.WasFlushed, Is.True);
        }
    }

    // The flag shares the state DB with the trie nodes it describes, so neither outlives the other.
    [Test]
    public void Reads_back_a_range_phase_finished_by_an_earlier_run()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory before = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        before.MarkRangePhaseFinished();

        PatriciaSnapTrieFactory afterRestart = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);

        Assert.That(afterRestart.IsRangePhaseFinished(), Is.True);
    }
}
