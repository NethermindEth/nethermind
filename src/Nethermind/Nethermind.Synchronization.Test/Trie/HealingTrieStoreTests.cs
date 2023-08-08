// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Synchronization.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.Trie;

public class HealingTrieStoreTests
{
    [Test]
    public void get_works()
    {
        TestMemDb db = new();
        db[TestItem.KeccakA.Bytes] = new byte[] { 1, 2 };
        HealingTrieStore healingTrieStore = new(db, Nethermind.Trie.Pruning.No.Pruning, Persist.EveryBlock, LimboLogs.Instance);
        healingTrieStore.LoadRlp(TestItem.KeccakA, ReadFlags.None);
    }

    [Test]
    public void recovery_works([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        byte[] rlp = { 1, 2 };
        Keccak key = TestItem.KeccakA;
        TestMemDb db = new();
        HealingTrieStore healingTrieStore = new(db, Nethermind.Trie.Pruning.No.Pruning, Persist.EveryBlock, LimboLogs.Instance);
        ITrieNodeRecovery<IReadOnlyList<Keccak>> recovery = Substitute.For<ITrieNodeRecovery<IReadOnlyList<Keccak>>>();
        recovery.CanRecover.Returns(isMainThread);
        recovery.Recover(key, Arg.Is<IReadOnlyList<Keccak>>(l => l.SequenceEqual(new[] { key })))
            .Returns(successfullyRecovered ? Task.FromResult<byte[]?>(rlp) : Task.FromResult<byte[]?>(null));

        healingTrieStore.InitializeNetwork(recovery);
        Action action = () => healingTrieStore.LoadRlp(key, ReadFlags.None);
        if (isMainThread && successfullyRecovered)
        {
            action.Should().NotThrow();
            db.KeyWasWritten(kvp => Bytes.AreEqual(kvp.Item1, key.Bytes) && Bytes.AreEqual(kvp.Item2, rlp));
        }
        else
        {
            action.Should().Throw<TrieNodeException>();
        }
    }
}
