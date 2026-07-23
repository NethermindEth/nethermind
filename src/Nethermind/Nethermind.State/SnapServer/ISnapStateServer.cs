// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.State.SnapServer;

/// <summary>
/// Serves the state-layout-specific portion of snap requests (trie nodes, account and storage ranges, byte codes).
/// Implemented once per state backend (Patricia trie vs flat), since only these reads depend on how state is stored.
/// </summary>
/// <remarks>
/// Block-access-list serving lives on <see cref="ISnapServer"/> instead: it depends on the block tree and the
/// block-access-list store rather than on state layout, and is composed on top of an <see cref="ISnapStateServer"/>.
/// </remarks>
public interface ISnapStateServer
{
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
