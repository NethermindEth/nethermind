// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Synchronization.SnapServer;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapBalServerTests
{
    private IBlockTree _blockTree = null!;
    private IBlockAccessListStore _store = null!;
    private SnapBalServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _blockTree = Substitute.For<IBlockTree>();
        _store = Substitute.For<IBlockAccessListStore>();
        _server = new SnapBalServer(_blockTree, _store);
    }

    private void GivenBlock(Hash256 hash, ulong number, byte[]? bal)
    {
        _blockTree.FindHeader(hash).Returns(Build.A.BlockHeader.WithNumber(number).TestObject);
        _store.GetRlp(number, hash).Returns(ArrayMemoryManager.From(bal));
    }

    [Test]
    public void GetBlockAccessLists_returns_rlp_for_known_blocks()
    {
        byte[] balA = [1, 2, 3];
        byte[] balB = [4, 5, 6, 7];
        GivenBlock(TestItem.KeccakA, 1, balA);
        GivenBlock(TestItem.KeccakB, 2, balB);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].ToArray(), Is.EqualTo(balA));
        Assert.That(result[1].ToArray(), Is.EqualTo(balB));
    }

    [Test]
    public void GetBlockAccessLists_skips_unknown_block()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);
        // KeccakB resolves to no header, so the store is never queried for it.
        _blockTree.FindHeader(TestItem.KeccakB).Returns((BlockHeader?)null);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].ToArray(), Is.EqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_skips_block_without_stored_list()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);
        // Header exists but no BAL is stored for it.
        GivenBlock(TestItem.KeccakB, 2, null);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].ToArray(), Is.EqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_respects_byte_limit()
    {
        GivenBlock(TestItem.KeccakA, 1, new byte[100]);
        GivenBlock(TestItem.KeccakB, 2, new byte[100]);

        // A byte limit below the first list stops the loop after a single entry is written.
        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256], 1, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
    }
}
