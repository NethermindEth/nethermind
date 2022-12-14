// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Consumers.Shared.Domain;

namespace Nethermind.DataMarketplace.Consumers.Shared
{
    public interface IConsumerGasLimitsService
    {
        GasLimits GasLimits { get; }
    }
}
