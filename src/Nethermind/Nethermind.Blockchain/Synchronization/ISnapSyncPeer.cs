// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.State.Snap;

namespace Nethermind.Blockchain.Synchronization
{
    public interface ISnapSyncPeer
    {
        Task<AccountsAndProofs> GetAccountRange(AccountRange range, CancellationToken token);
        Task<SlotsAndProofs> GetStorageRange(StorageRange range, CancellationToken token);
        Task<IReadOnlyList<byte[]>> GetByteCodes(Keccak[] codeHashes, CancellationToken token);
        Task<IReadOnlyList<byte[]>> GetTrieNodes(AccountsToRefreshRequest request, CancellationToken token);
        Task<IReadOnlyList<byte[]>> GetTrieNodes(GetTrieNodesRequest request, CancellationToken token);
    }
}
