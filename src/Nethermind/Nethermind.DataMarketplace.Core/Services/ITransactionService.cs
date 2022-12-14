// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface ITransactionService
    {
        Task<Keccak> UpdateGasPriceAsync(Keccak transactionHash, UInt256 gasPrice);
        Task<Keccak> UpdateValueAsync(Keccak transactionHash, UInt256 value);
        Task<CanceledTransactionInfo> CancelAsync(Keccak transactionHash);
    }
}
