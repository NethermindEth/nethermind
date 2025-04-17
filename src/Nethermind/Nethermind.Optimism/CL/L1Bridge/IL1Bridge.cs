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
    Task<L1Block> GetBlock(ulong blockNumber, CancellationToken token);
    Task<L1Block> GetBlockByHash(Hash256 blockHash, CancellationToken token);
    Task<ReceiptForRpc[]> GetReceiptsByBlockHash(Hash256 blockHash, CancellationToken token);
    void Reset(L1BlockInfo highestFinalizedOrigin);
}
