// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Consumers.Refunds;
using Nethermind.DataMarketplace.Consumers.Shared.Domain;
using Nethermind.DataMarketplace.Core.Services;

namespace Nethermind.DataMarketplace.Consumers.Shared.Services
{
    public class ConsumerGasLimitsService : IConsumerGasLimitsService
    {
        public ConsumerGasLimitsService(IDepositService depositService, IRefundService refundService)
        {
            GasLimits = new GasLimits(depositService.GasLimit, refundService.GasLimit);
        }

        public GasLimits GasLimits { get; }
    }
}
