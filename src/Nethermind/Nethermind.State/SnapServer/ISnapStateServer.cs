// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.State.SnapServer;

public interface ISnapStateServer
{
    const long HardResponseByteLimit = 2000000;
    const int HardResponseNodeLimit = 100000;

    /// <summary>
    /// Caps the number of trie traversals a single <see cref="GetTrieNodes(IReadOnlyList{PathGroup}, Hash256, long, CancellationToken)"/> may perform.
    /// </summary>
    /// <remarks>
    /// The response size measured against <see cref="HardResponseByteLimit"/> only advances for paths that
    /// resolve to a node, so on its own it does not bound a request whose paths resolve to nothing. This cap
    /// keeps such a request no more expensive than a productive one, and matches the cap on how many nodes a
    /// <c>TrieNodes</c> response may carry; snap/1 lets a server answer with fewer nodes than were asked for.
    /// </remarks>
    const int MaxTrieNodeLookups = 4096;

    bool CanServe { get; }

    IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        GetTrieNodes(pathSet, rootHash, long.MaxValue, cancellationToken);

    IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit, CancellationToken cancellationToken);

    (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken);

    (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken);
}
