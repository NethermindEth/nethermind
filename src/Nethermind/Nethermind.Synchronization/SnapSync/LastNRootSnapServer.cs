// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Utils;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapSync;

public class LastNRootSnapServer(ISnapServer baseSnapServer, ILastNStateRootTracker stateRootTracker): ISnapServer
{
    private bool IsRootMissing(in ValueHash256 stateRoot)
    {
        return !stateRootTracker.HasStateRoot(stateRoot.ToCommitment());
    }

    public IOwnedReadOnlyList<byte[]>? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, in ValueHash256 rootHash, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return ArrayPoolList<byte[]>.Empty();
        return baseSnapServer.GetTrieNodes(pathSet, in rootHash, cancellationToken);
    }

    public IOwnedReadOnlyList<byte[]> GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        return baseSnapServer.GetByteCodes(requestedHashes, byteLimit, cancellationToken);
    }

    public (IOwnedReadOnlyList<PathWithAccount>, IOwnedReadOnlyList<byte[]>) GetAccountRanges(in ValueHash256 rootHash, in ValueHash256 startingHash,
        in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (ArrayPoolList<PathWithAccount>.Empty(), ArrayPoolList<byte[]>.Empty());
        return baseSnapServer.GetAccountRanges(in rootHash, in startingHash, in limitHash, byteLimit, cancellationToken);
    }

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IOwnedReadOnlyList<byte[]>?) GetStorageRanges(in ValueHash256 rootHash, IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash, in ValueHash256? limitHash, long byteLimit, CancellationToken cancellationToken)
    {
        if (IsRootMissing(rootHash)) return (ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>.Empty(), ArrayPoolList<byte[]>.Empty());
        return baseSnapServer.GetStorageRanges(in rootHash, accounts, in startingHash, in limitHash, byteLimit, cancellationToken);
    }
}
