// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISnapSyncPeer
    {
        Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token);
        Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token);
        Task<IOwnedReadOnlyList<byte[]>> GetByteCodes(IReadOnlyList<ValueHash256> codeHashes, CancellationToken token);
        Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token);
        Task<IOwnedReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token);
    }
}
