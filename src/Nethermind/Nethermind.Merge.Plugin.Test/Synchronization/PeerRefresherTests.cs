//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Stats.Model;
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
        _headParentBlockHeader = Build.A.BlockHeader.WithExtraData(new byte []{ 0 }).TestObject;
        _headBlockHeader = Build.A.BlockHeader.WithParent(_headParentBlockHeader).TestObject;
        _finalizedBlockHeader = Build.A.BlockHeader.WithExtraData(new byte []{ 1 }).TestObject;

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
