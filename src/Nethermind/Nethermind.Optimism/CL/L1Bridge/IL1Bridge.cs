// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Optimism.CL.Derivation;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IL1Bridge
{
    Task Run(CancellationToken token);
    Task<BlobSidecar[]> GetBlobSidecars(ulong slotNumber, int indexFrom, int indexTo);
    Task<L1Block> GetBlock(ulong blockNumber);
    Task<L1Block> GetBlockByHash(Hash256 blockHash);
    Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash);
    // For testing purposes. Will trigger L1 traversal
    void Reset(L1BlockInfo highestFinalizedOrigin);
}
