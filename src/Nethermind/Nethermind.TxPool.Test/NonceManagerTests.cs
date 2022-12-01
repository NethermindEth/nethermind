// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

public class NonceManagerTests
{
    private ISpecProvider _specProvider;
    private IStateProvider _stateProvider;
    private IBlockTree _blockTree;
    private ChainHeadInfoProvider _headInfo;
    private INonceManager _nonceManager;

    [SetUp]
    public void Setup()
    {
        ILogManager logManager = LimboLogs.Instance;
        _specProvider = RopstenSpecProvider.Instance;
        var trieStore = new TrieStore(new MemDb(), logManager);
        var codeDb = new MemDb();
        _stateProvider = new StateProvider(trieStore, codeDb, logManager);
        _blockTree = Substitute.For<IBlockTree>();
        Block block = Build.A.Block.WithNumber(0).TestObject;
        _blockTree.Head.Returns(block);
        _blockTree.FindBestSuggestedHeader().Returns(Build.A.BlockHeader.WithNumber(10000000).TestObject);

        _headInfo = new ChainHeadInfoProvider(_specProvider, _blockTree, _stateProvider);
        _nonceManager = new NonceManager(_headInfo.AccountStateProvider);
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations()
    {


        var nonceA1 = _nonceManager.ReserveNonce(TestItem.AddressA);
        _nonceManager.TxAccepted(TestItem.AddressA);
        var nonceA2 = _nonceManager.ReserveNonce(TestItem.AddressA);
        _nonceManager.TxRejected(TestItem.AddressA);
        var nonceA3 = _nonceManager.ReserveNonce(TestItem.AddressA);
        _nonceManager.TxAccepted(TestItem.AddressA);
        var nonceB1 = _nonceManager.ReserveNonce(TestItem.AddressB);
        _nonceManager.TxAccepted(TestItem.AddressB);
        var nonceB2 = _nonceManager.ReserveNonce(TestItem.AddressB);
        _nonceManager.TxAccepted(TestItem.AddressB);
        var nonceB3 = _nonceManager.ReserveNonce(TestItem.AddressB);
        _nonceManager.TxRejected(TestItem.AddressB);
        var nonceB4 = _nonceManager.ReserveNonce(TestItem.AddressB);
        _nonceManager.TxAccepted(TestItem.AddressB);

        nonceA1.Should().Be(0);
        nonceA2.Should().Be(1);
        nonceA3.Should().Be(1);
        nonceB1.Should().Be(0);
        nonceB2.Should().Be(1);
        nonceB3.Should().Be(2);
        nonceB4.Should().Be(2);
    }

    [Test]
    public void should_increment_own_transaction_nonces_locally_when_requesting_reservations_in_parallel()
    {
        const int reservationsCount = 1000;

        ConcurrentQueue<UInt256> nonces = new();

        var result = Parallel.For(0, reservationsCount, i =>
        {
            UInt256 nonce = _nonceManager.ReserveNonce(TestItem.AddressA);
            _nonceManager.TxAccepted(TestItem.AddressA);
            nonces.Enqueue(nonce);
        });

        result.IsCompleted.Should().BeTrue();
        UInt256 nonce = _nonceManager.ReserveNonce(TestItem.AddressA);
        nonces.Enqueue(nonce);
        nonce.Should().Be(new UInt256(reservationsCount));
        nonces.OrderBy(n => n).Should().BeEquivalentTo(Enumerable.Range(0, reservationsCount + 1).Select(i => new UInt256((uint)i)));
    }
}
