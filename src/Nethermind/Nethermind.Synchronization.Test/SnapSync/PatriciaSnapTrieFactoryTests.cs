// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
    public void Records_completed_account_range_phase_in_the_state_db()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory factory = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        Assert.That(factory.IsAccountRangePhaseCompleted(), Is.False);

        factory.MarkAccountRangePhaseCompleted();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateDb[AccountProgressKey], Is.EqualTo(Keccak.MaxValue.BytesToArray()));
            Assert.That(stateDb.WasFlushed, Is.True);
        }
    }

    // The flag shares the state DB with the trie nodes it describes, so neither outlives the other.
    [Test]
    public void Reads_back_a_phase_completed_by_an_earlier_run()
    {
        TestMemDb stateDb = new();
        PatriciaSnapTrieFactory before = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);
        before.MarkAccountRangePhaseCompleted();

        PatriciaSnapTrieFactory afterRestart = new(new NodeStorage(stateDb), stateDb, LimboLogs.Instance);

        Assert.That(afterRestart.IsAccountRangePhaseCompleted(), Is.True);
    }
}
