// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Blockchain.Synchronization;

public interface IVerkleSyncPeer
{
    Task<SubTreesAndProofs> GetSubTreeRange(SubTreeRange range, CancellationToken token);
    Task<byte[][]> GetLeafNodes(GetLeafNodesRequest request, CancellationToken token);
    Task<byte[][]> GetLeafNodes(LeafToRefreshRequest request, CancellationToken token);
}
