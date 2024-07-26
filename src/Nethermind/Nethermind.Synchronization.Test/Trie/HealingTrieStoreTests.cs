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
        NodeStorage storage = new NodeStorage(db);
        storage.Set(null, TreePath.Empty, TestItem.KeccakA, new byte[] { 1, 2 });
        HealingTrieStore healingTrieStore = new(storage, Nethermind.Trie.Pruning.No.Pruning, Persist.EveryBlock, LimboLogs.Instance);
        healingTrieStore.LoadRlp(null, TreePath.Empty, TestItem.KeccakA);
    }

    [Test]
    public void recovery_works([Values(true, false)] bool isMainThread, [Values(true, false)] bool successfullyRecovered)
    {
        byte[] rlp = { 1, 2 };
        Hash256 hash = TestItem.KeccakA;
        byte[] key = NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, hash);

        TestMemDb db = new();
        HealingTrieStore healingTrieStore = new(new NodeStorage(db), Nethermind.Trie.Pruning.No.Pruning, Persist.EveryBlock, LimboLogs.Instance);
        ITrieNodeRecovery<IReadOnlyList<Hash256>> recovery = Substitute.For<ITrieNodeRecovery<IReadOnlyList<Hash256>>>();
        recovery.CanRecover.Returns(isMainThread);
        recovery.Recover(hash, Arg.Is<IReadOnlyList<Hash256>>(l => l.SequenceEqual(new[] { hash })))
            .Returns(successfullyRecovered ? Task.FromResult<byte[]?>(rlp) : Task.FromResult<byte[]?>(null));

        healingTrieStore.InitializeNetwork(recovery);
        Action action = () => healingTrieStore.LoadRlp(null, TreePath.Empty, hash, ReadFlags.None);
        if (isMainThread && successfullyRecovered)
        {
            action.Should().NotThrow();
            db.KeyWasWritten(kvp => Bytes.AreEqual(kvp.Item1, key) && Bytes.AreEqual(kvp.Item2, rlp));
        }
        else
        {
            action.Should().Throw<TrieNodeException>();
        }
    }
}
