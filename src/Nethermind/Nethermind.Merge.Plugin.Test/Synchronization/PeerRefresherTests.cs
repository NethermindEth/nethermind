// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test.Synchronization;

public class PeerRefresherTests
{
    private BlockHeader _headBlockHeader = null!;
    private BlockHeader _headParentBlockHeader = null!;
    private BlockHeader _finalizedBlockHeader = null!;
    private PeerRefresher _peerRefresher = null!;
    private IPeerDifficultyRefreshPool _syncPeerPool = null!;
    private ISyncPeer _syncPeer = null!;

    [SetUp]
    public void Setup()
    {
        _headParentBlockHeader = Build.A.BlockHeader.WithExtraData(new byte[] { 0 }).TestObject;
        _headBlockHeader = Build.A.BlockHeader.WithParent(_headParentBlockHeader).TestObject;
        _finalizedBlockHeader = Build.A.BlockHeader.WithExtraData(new byte[] { 1 }).TestObject;

        _syncPeerPool = Substitute.For<IPeerDifficultyRefreshPool>();
        _syncPeer = Substitute.For<ISyncPeer>();
        _peerRefresher = new PeerRefresher(_syncPeerPool, new TimerFactory(), new TestLogManager());
    }

    [Test]
    public async Task Given_allHeaderAvailable_thenShouldCallUpdateHeader_3Times()
    {
        GivenAllHeaderAvailable();

        await WhenCalledWithCorrectHash();

        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headParentBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _finalizedBlockHeader);
        _syncPeerPool.DidNotReceive().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }

    [Test]
    public async Task Given_headBlockNotAvailable_thenShouldCallUpdateHeader_forFinalizedBlockOnly()
    {
        GivenFinalizedHeaderAvailable();

        await WhenCalledWithCorrectHash();

        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headParentBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _finalizedBlockHeader);
        _syncPeerPool.DidNotReceive().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }

    [Test]
    public async Task Given_finalizedBlockNotAvailable_thenShouldCallRefreshFailed()
    {
        await WhenCalledWithCorrectHash();

        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _headParentBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, _finalizedBlockHeader);
        _syncPeerPool.Received().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }

    private Task WhenCalledWithCorrectHash()
    {
        CancellationTokenSource source = new(1000);
        return _peerRefresher.RefreshPeerForFcu(
            _syncPeer,
            _headBlockHeader.Hash!,
            _headParentBlockHeader.Hash!,
            _finalizedBlockHeader.Hash!,
            source.Token);
    }

    private void GivenAllHeaderAvailable()
    {
        _syncPeer.GetBlockHeaders(_headParentBlockHeader.Hash!, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { _headParentBlockHeader, _headBlockHeader }));
        GivenFinalizedHeaderAvailable();
    }

    private void GivenFinalizedHeaderAvailable() =>
        _syncPeer.GetHeadBlockHeader(_finalizedBlockHeader.Hash!, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_finalizedBlockHeader));
}
