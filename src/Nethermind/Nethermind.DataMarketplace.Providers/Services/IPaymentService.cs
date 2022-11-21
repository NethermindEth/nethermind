// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IPaymentService
    {
        ulong GasLimit { get; }
        Task<Keccak?> ClaimPaymentAsync(PaymentClaim paymentClaim, Address coldWalletAddress, UInt256 gasPrice);
    }
}
