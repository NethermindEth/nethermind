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

#nullable enable
using System;
using System.Collections;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Network;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test
{
    [Parallelizable(ParallelScope.All)]
    public class PeerInfoAllocationTests
    {
        [TestCase("Nethermind/v1.10.71-0-13221de89-20211103/X64-Linux/5.0.5", AllocationContexts.All, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.0-rc.1-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.2-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.3.0-rc.3-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.All & ~AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.9-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/xdai-friends/v3.3.0-rc.10-stable-d8305c5-20210903/x86_64-linux-gnu", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.2.5-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.1.1-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = false)]
        [TestCase("OpenEthereum/v3.0.0-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.0-rc.4-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.0-rc.7-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.0-rc.11-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]
        [TestCase("OpenEthereum/v3.3.0-rc.15-stable/x86_64-linux-musl/rustc1.47.0", AllocationContexts.State, ExpectedResult = true)]
        public bool SupportsAllocation(string versionString, AllocationContexts contexts)
        {
            PeerInfo peerInfo = new(SetupSyncPeer(versionString));
            return peerInfo.CanBeAllocated(contexts);
        }

        public static IEnumerable OpenEthereumVersionTests
        {
            get
            {
                yield return new TestCaseData("Nethermind/v1.10.71-0-13221de89-20211103/X64-Linux/5.0.5", null, 0);
                yield return new TestCaseData("OpenEthereum/xdai-friends/v3.3.0-rc.8-stable-d8305c5-20210903/x86_64-linux-gnu", new Version(3, 3, 0), 8);
                yield return new TestCaseData("OpenEthereum/v3.3.0-rc.15-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 15);
                yield return new TestCaseData("OpenEthereum/v3.3.0-rc.13-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 13);
                yield return new TestCaseData("OpenEthereum/v3.2.5-stable-32d8b54-20210505/x86_64-linux-gnu/rustc1.51.0", new Version(3, 2, 5), 0);
                yield return new TestCaseData("OpenEthereum/pocket-foundation-1/v3.3.0-rc.7-stable/x86_64-linux-musl/rustc1.47.0", new Version(3, 3, 0), 7);
            }
        }
        
        [TestCaseSource(nameof(OpenEthereumVersionTests))]
        public void GetOpenEthereumVersion(string versionString, Version? expectedVersion = null, int expectedReleaseCandidate = 0)
        {
            ISyncPeer peer = SetupSyncPeer(versionString);

            Version? version = peer.GetOpenEthereumVersion(out int releaseCandidate);
            version.Should().Be(expectedVersion);
            releaseCandidate.Should().Be(expectedReleaseCandidate);
        }

        private static ISyncPeer SetupSyncPeer(string versionString)
        {
            ISyncPeer peer = Substitute.For<ISyncPeer>();
            peer.ClientId.Returns(versionString);
            peer.ClientType.Returns(
                versionString.Contains("OpenEthereum", StringComparison.InvariantCultureIgnoreCase)
                    ? NodeClientType.OpenEthereum
                    : NodeClientType.Nethermind);
            return peer;
        }
    }
}
