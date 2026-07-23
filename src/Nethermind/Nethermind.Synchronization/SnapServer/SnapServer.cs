// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapServer;

/// <summary>Composes the state-layout-specific <see cref="ISnapStateServer"/> with the layout-independent code and
/// block-access-list servers into a full <see cref="ISnapServer"/>.</summary>
public sealed class SnapServer(ISnapStateServer stateServer, ISnapCodeServer codeServer, ISnapBalServer balServer) : ISnapServer
{
    public bool CanServe => stateServer.CanServe;

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, cancellationToken);

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, byteLimit, cancellationToken);

    public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken) =>
        codeServer.GetByteCodes(requestedHashes, byteLimit, cancellationToken);

    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken) =>
        balServer.GetBlockAccessLists(blockHashes, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetAccountRanges(rootHash, startingHash, limitHash, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetStorageRanges(rootHash, accounts, startingHash, limitHash, byteLimit, cancellationToken);
}
