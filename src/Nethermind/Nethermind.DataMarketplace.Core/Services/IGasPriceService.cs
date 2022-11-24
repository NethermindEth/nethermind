// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Services.Models;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IGasPriceService
    {
        GasPriceTypes? Types { get; }
        Task<UInt256> GetCurrentGasPriceAsync();
        Task<UInt256> GetCurrentRefundGasPriceAsync();
        Task<UInt256> GetCurrentPaymentClaimGasPriceAsync();
        Task SetGasPriceOrTypeAsync(string gasPriceOrType);
        Task SetRefundGasPriceAsync(UInt256 gasPrice);
        Task SetPaymentClaimGasPriceAsync(UInt256 gasPrice);
        Task UpdateGasPriceAsync();
    }
}
