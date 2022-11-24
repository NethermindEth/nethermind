// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface INdmBlockchainBridge
    {
        Task<long> GetLatestBlockNumberAsync();
        Task<byte[]> GetCodeAsync(Address address);
        Task<Block?> FindBlockAsync(Keccak blockHash);
        Task<Block?> FindBlockAsync(long blockNumber);
        Task<Block?> GetLatestBlockAsync();
        Task<UInt256> GetNonceAsync(Address address);
        Task<NdmTransaction?> GetTransactionAsync(Keccak transactionHash);
        Task<ulong> GetNetworkIdAsync();
        Task<byte[]> CallAsync(Transaction transaction);
        Task<byte[]> CallAsync(Transaction transaction, long blockNumber);
        ValueTask<Keccak?> SendOwnTransactionAsync(Transaction transaction);
    }
}
