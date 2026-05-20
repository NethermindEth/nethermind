// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.State.SnapServer;

public class NoopSnapServer : ISnapServer
{
    public static readonly NoopSnapServer Instance = new();

    public bool CanServe => false;

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        EmptyByteArrayList.Instance;

    public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken) =>
        EmptyByteArrayList.Instance;

    public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
        in ValueHash256 startingHash, in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken) =>
        (ArrayPoolList<PathWithAccount>.Empty(), EmptyByteArrayList.Instance);

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash, IReadOnlyList<PathWithAccount> accounts, in ValueHash256? startingHash,
        in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken) =>
        (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), EmptyByteArrayList.Instance);
}
