// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.L1Bridge;

public interface IEthApi
{
    Task<ReceiptForRpc[]?> GetReceiptsByHash(Hash256 blockHash);
    Task<L1Block?> GetBlockByHash(Hash256 blockHash, bool fullTxs);
    Task<L1Block?> GetBlockByNumber(ulong blockNumber, bool fullTxs);
    Task<L1Block?> GetHead(bool fullTxs);
    Task<L1Block?> GetFinalized(bool fullTxs);
    Task<L1Block?> GetSafe(bool fullTxs);
    Task<ulong> GetChainId();
}

