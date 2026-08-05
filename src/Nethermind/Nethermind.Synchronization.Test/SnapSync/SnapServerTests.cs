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
using Nethermind.Db;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapServerTests
{
    private MemDb _codeDb = null!;
    private IBlockTree _blockTree = null!;
    private IBlockAccessListStore _balStore = null!;
    private SnapServer _server = null!;

    [SetUp]
    public void SetUp()
    {
        _codeDb = new MemDb();
        _blockTree = Substitute.For<IBlockTree>();
        _balStore = Substitute.For<IBlockAccessListStore>();
        _server = new SnapServer(NoopSnapServer.Instance, _codeDb, _blockTree, _balStore);
    }

    [TearDown]
    public void TearDown() => _codeDb.Dispose();

    private ValueHash256 StoreCode(byte[] code)
    {
        Hash256 hash = Keccak.Compute(code);
        _codeDb[hash.Bytes] = code;
        return hash.ValueHash256;
    }

    private void GivenBlock(Hash256 hash, ulong number, byte[]? bal)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(number)
            .WithBlockAccessListHash(TestItem.KeccakA)
            .TestObject;
        _blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(header);
        _balStore.GetRlp(number, hash).Returns(ArrayMemoryManager.From(bal));
    }

    [Test]
    public void GetByteCodes_returns_requested_codes_in_order()
    {
        byte[] codeA = [1, 2, 3];
        byte[] codeB = [4, 5, 6, 7];
        ValueHash256 hashA = StoreCode(codeA);
        ValueHash256 hashB = StoreCode(codeB);

        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].ToArray(), Is.EqualTo(codeA));
        Assert.That(result[1].ToArray(), Is.EqualTo(codeB));
    }

    [Test]
    public void GetByteCodes_empty_string_hash_returns_empty_entry()
    {
        using IByteArrayList result = _server.GetByteCodes(
            [Keccak.OfAnEmptyString.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Length, Is.EqualTo(0));
    }

    [Test]
    public void GetByteCodes_skips_missing_code()
    {
        byte[] code = [1, 2, 3];
        ValueHash256 present = StoreCode(code);
        ValueHash256 missing = Keccak.Compute([9, 9, 9]).ValueHash256;

        using IByteArrayList result = _server.GetByteCodes([missing, present], long.MaxValue, CancellationToken.None);

        // The missing hash contributes no entry, so only the present code is returned.
        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].ToArray(), Is.EqualTo(code));
    }

    [Test]
    public void GetByteCodes_respects_byte_limit()
    {
        ValueHash256 hashA = StoreCode(new byte[100]);
        ValueHash256 hashB = StoreCode(new byte[100]);

        // A byte limit smaller than the first code stops the loop after a single entry is written.
        using IByteArrayList result = _server.GetByteCodes([hashA, hashB], 1, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
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
    public void GetBlockAccessLists_returns_empty_entry_for_unknown_block()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);
        // KeccakB resolves to no header, so the store is never queried for it.
        _blockTree.FindHeader(TestItem.KeccakB, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns((BlockHeader?)null);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Length, Is.EqualTo(0));
        Assert.That(result[1].ToArray(), Is.EqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_returns_empty_entry_for_block_without_stored_list()
    {
        byte[] bal = [1, 2, 3];
        GivenBlock(TestItem.KeccakA, 1, bal);
        // Header exists but no BAL is stored for it.
        GivenBlock(TestItem.KeccakB, 2, null);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakB.ValueHash256, TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(2));
        Assert.That(result[0].Length, Is.EqualTo(0));
        Assert.That(result[1].ToArray(), Is.EqualTo(bal));
    }

    [Test]
    public void GetBlockAccessLists_returns_empty_entry_for_block_predating_access_lists()
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(1).WithBlockAccessListHash(null).TestObject;
        _blockTree.FindHeader(TestItem.KeccakA, BlockTreeLookupOptions.TotalDifficultyNotNeeded).Returns(header);

        using IByteArrayList result = _server.GetBlockAccessLists(
            [TestItem.KeccakA.ValueHash256], long.MaxValue, CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(1));
        Assert.That(result[0].Length, Is.EqualTo(0));
        _balStore.DidNotReceiveWithAnyArgs().GetRlp(default, default!);
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
