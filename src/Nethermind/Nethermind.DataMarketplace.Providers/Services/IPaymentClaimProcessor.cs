// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IPaymentClaimProcessor
    {
        Task<PaymentClaim?> ProcessAsync(DataDeliveryReceiptRequest receiptRequest, Signature signature);
        Task<Keccak?> SendTransactionAsync(PaymentClaim paymentClaim, UInt256 gasPrice);
        void ChangeColdWalletAddress(Address address);
    }
}
