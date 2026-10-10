// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.Test;

public class MockSnapSyncPeer(ISnapServer snapServer) : ISnapSyncPeer
{
    private static readonly byte[] _emptyBytes = [0];

    public byte SnapProtocolVersion => SnapVersions.Snap1;
    public Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
    {
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = snapServer.GetAccountRanges(
            range.RootHash,
            range.StartingHash,
            range.LimitHash,
            int.MaxValue,
            token);

        return Task.FromResult(new AccountsAndProofs()
        {
            PathAndAccounts = accounts,
            Proofs = proofs,
        });
    }

    public Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token)
    {
        Hash256 rootHash = range.RootHash
            ?? throw new InvalidOperationException("A storage range request must have a root hash before it is dispatched.");
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proof) = snapServer.GetStorageRanges(
            rootHash,
            range.Accounts,
            range.StartingHash,
            range.LimitHash,
            int.MaxValue,
            token);

        return Task.FromResult(new SlotsAndProofs()
        {
            PathsAndSlots = slots,
            Proofs = proof,
        });
    }

    public Task<IByteArrayList> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
    {
        IByteArrayList codes = snapServer.GetByteCodes(codeHashes, int.MaxValue, token);
        return Task.FromResult(codes);
    }

    public Task<IByteArrayList> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
    {
        PathGroup[] groups = new PathGroup[request.Paths.Count];
        for (int i = 0; i < request.Paths.Count; i++)
        {
            AccountWithStorageStartingHash path = request.Paths[i];
            PathWithAccount pathAndAccount = path.PathAndAccount
                ?? throw new InvalidOperationException("An account refresh request must have an account path before it is dispatched.");
            groups[i] = new PathGroup { Group = [pathAndAccount.Path.Bytes.ToArray(), _emptyBytes] };
        }

        using RlpPathGroupList encoded = PathGroup.EncodeToRlpPathGroupList(groups);
        IByteArrayList? res = snapServer.GetTrieNodes(encoded, request.RootHash, token);
        return Task.FromResult(res ?? EmptyByteArrayList.Instance);
    }

    public Task<IByteArrayList> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
    {
        IByteArrayList? res = snapServer.GetTrieNodes(request.AccountAndStoragePaths, request.RootHash, token);
        return Task.FromResult(res ?? EmptyByteArrayList.Instance);
    }
}
