// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IEthApi
{
    Task<ReceiptForRpc[]?> GetReceiptsByHash(Hash256 blockHash);
    Task<BlockForRpc?> GetBlockByHash(Hash256 blockHash, bool fullTxs);
    Task<BlockForRpc?> GetBlockByNumber(ulong blockNumber, bool fullTxs);
}
