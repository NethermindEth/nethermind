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

using System;
using System.Text.RegularExpressions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Stats.Model;

namespace Nethermind.Synchronization.Peers
{
    public static class SyncPeerExtensions
    {
        // Check if OpenEthereum supports GetNodeData
        private static readonly Version _openEthereumSecondRemoveGetNodeDataVersion = new(3, 3, 0);
        private static readonly Version _openEthereumFirstRemoveGetNodeDataVersion = new(3, 1, 0);
        
        public static bool SupportsAllocation(this PeerInfo peerInfo, AllocationContexts contexts)
        {
            // only for State allocations
            if ((contexts & AllocationContexts.State) != 0)
            {
                // try get OpenEthereum version
                Version? openEthereumVersion = peerInfo.SyncPeer.GetOpenEthereumVersion(out int releaseCandidate);
                if (openEthereumVersion is not null)
                {
                    int versionComparision = openEthereumVersion.CompareTo(_openEthereumSecondRemoveGetNodeDataVersion);
                    switch (versionComparision)
                    {
                        case < 0:
                            return openEthereumVersion < _openEthereumFirstRemoveGetNodeDataVersion;
                        case 0:
                            switch (releaseCandidate)
                            {
                                case <= 3:
                                case >= 8 and <= 10:
                                    return false;
                                // > 10, we should support only for AuRa, but we can ignore it for now
                                default:
                                    return true;
                            }
                    }
                }
            }

            return true;
        }
        
        private static readonly Regex _openEthereumVersionRegex = new(@"OpenEthereum\/([a-zA-z-0-9]*\/)*v(?<version>(?<mainVersion>[0-9]\.[0-9]\.[0-9])-(rc\.(?<rc>[0-9]*))?)", RegexOptions.Compiled);
        
        public static Version? GetOpenEthereumVersion(this ISyncPeer peer, out int releaseCandidate)
        {
            if (peer.ClientType == NodeClientType.OpenEthereum)
            {
                Match match = _openEthereumVersionRegex.Match(peer.ClientId);

                if (match.Success && Version.TryParse(match.Groups["mainVersion"].Value, out Version version))
                {
                    int.TryParse(match.Groups["rc"].Value, out releaseCandidate);
                    return version;
                }
            }
            
            releaseCandidate = 0;
            return null;
        }
    }
}
