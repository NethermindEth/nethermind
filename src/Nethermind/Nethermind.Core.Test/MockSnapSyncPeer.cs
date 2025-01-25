// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.Test;

public class MockSnapSyncPeer(ISnapServer snapServer) : ISnapSyncPeer
{
    private static readonly byte[] _emptyBytes = [0];
    public Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token)
    {
        (IOwnedReadOnlyList<PathWithAccount> accounts, IOwnedReadOnlyList<byte[]> proofs) = snapServer.GetAccountRanges(
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
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IOwnedReadOnlyList<byte[]>? proof) = snapServer.GetStorageRanges(
            range.RootHash,
            range.Accounts,
            range.StartingHash,
            range.LimitHash,
            int.MaxValue,
            token);

        return Task.FromResult(new SlotsAndProofs()
        {
            PathsAndSlots = slots,
            Proofs = proof!,
        });
    }

    public Task<IOwnedReadOnlyList<byte[]>> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token)
    {
        IOwnedReadOnlyList<byte[]> codes = snapServer.GetByteCodes(codeHashes, int.MaxValue, token);
        return Task.FromResult(codes);
    }

    public Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token)
    {
        ArrayPoolList<PathGroup> groups = new(request.Paths.Count);

        for (int i = 0; i < request.Paths.Count; i++)
        {
            AccountWithStorageStartingHash path = request.Paths[i];
            groups.Add(new PathGroup { Group = [path.PathAndAccount.Path.Bytes.ToArray(), _emptyBytes] });
        }

        IOwnedReadOnlyList<byte[]>? res = snapServer.GetTrieNodes(groups, request.RootHash, token);
        return Task.FromResult(res!);
    }

    public Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token)
    {
        IOwnedReadOnlyList<byte[]>? res = snapServer.GetTrieNodes(request.AccountAndStoragePaths, request.RootHash, token);
        return Task.FromResult(res!);
    }
}
