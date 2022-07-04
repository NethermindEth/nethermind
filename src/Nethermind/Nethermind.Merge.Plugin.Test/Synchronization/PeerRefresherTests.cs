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
    private BlockHeader headBlockHeader;
    private BlockHeader headParentBlockHeader;
    private BlockHeader finalizedBlockHeader;
    private PeerRefresher _peerRefresher;
    private ISyncPeerPool _syncPeerPool;
    private ISyncPeer _syncPeer;

    [SetUp]
    public void Setup()
    {
        headParentBlockHeader = Build.A.BlockHeader.WithExtraData(new byte []{ 0 }).TestObject;
        headBlockHeader = Build.A.BlockHeader.WithParent(headParentBlockHeader).TestObject;
        finalizedBlockHeader = Build.A.BlockHeader.WithExtraData(new byte []{ 1 }).TestObject;

        _syncPeerPool = Substitute.For<ISyncPeerPool>();
        _syncPeer = Substitute.For<ISyncPeer>();
        _peerRefresher = new PeerRefresher(_syncPeerPool, new TimerFactory(), new TestLogManager());
    }

    [Test]
    public async Task Given_allHeaderAvailable_thenShouldCallUpdateHeader_3Times()
    {
        GivenAllHeaderAvailable();

        WhenCalledWithCorrectHash();
        
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headParentBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, finalizedBlockHeader);
        _syncPeerPool.DidNotReceive().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }

    [Test]
    public async Task Given_headBlockNotAvailable_thenShouldCallUpdateHeader_forFinalizedBlockOnly()
    {
        GivenFinalizedHeaderAvailable();

        WhenCalledWithCorrectHash();
        
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headParentBlockHeader);
        _syncPeerPool.Received().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, finalizedBlockHeader);
        _syncPeerPool.DidNotReceive().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }
    
    [Test]
    public async Task Given_finalizedBlockNotAvailable_thenShouldCallRefreshFailed()
    {
        WhenCalledWithCorrectHash();
        
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, headParentBlockHeader);
        _syncPeerPool.DidNotReceive().UpdateSyncPeerHeadIfHeaderIsBetter(_syncPeer, finalizedBlockHeader);
        _syncPeerPool.Received().ReportRefreshFailed(_syncPeer, Arg.Any<string>());
    }

    private Task WhenCalledWithCorrectHash()
    {
        return _peerRefresher.RefreshPeerForFcu(
            _syncPeer,
            headBlockHeader.Hash,
            headParentBlockHeader.Hash,
            finalizedBlockHeader.Hash, 
            Task.Delay(1000),
            new CancellationToken());
    }
    
    private void GivenAllHeaderAvailable()
    {
        _syncPeer.GetBlockHeaders(headParentBlockHeader.Hash, 2, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new[] { headParentBlockHeader, headBlockHeader }));
        GivenFinalizedHeaderAvailable();
    }
    
    private void GivenFinalizedHeaderAvailable()
    {
        _syncPeer.GetHeadBlockHeader(finalizedBlockHeader.Hash, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(finalizedBlockHeader));
    }
    
}
