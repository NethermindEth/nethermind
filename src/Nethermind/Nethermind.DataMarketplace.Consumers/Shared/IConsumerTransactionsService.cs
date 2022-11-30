// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Consumers.Shared
{
    public interface IConsumerTransactionsService
    {
        Task<IEnumerable<ResourceTransaction>> GetAllTransactionsAsync();
        Task<IEnumerable<ResourceTransaction>> GetPendingAsync();
        Task<UpdatedTransactionInfo> UpdateDepositGasPriceAsync(Keccak depositId, UInt256 gasPrice);
        Task<UpdatedTransactionInfo> UpdateRefundGasPriceAsync(Keccak depositId, UInt256 gasPrice);
        Task<UpdatedTransactionInfo> CancelDepositAsync(Keccak depositId);
        Task<UpdatedTransactionInfo> CancelRefundAsync(Keccak depositId);
    }
}
