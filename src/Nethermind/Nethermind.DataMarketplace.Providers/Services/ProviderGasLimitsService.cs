// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public class ProviderGasLimitsService : IProviderGasLimitsService
    {
        public ProviderGasLimitsService(IPaymentService paymentService)
        {
            GasLimits = new GasLimits(paymentService.GasLimit);
        }

        public GasLimits GasLimits { get; }
    }
}
