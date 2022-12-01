// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits
{
    public interface IDepositUnitsCalculator
    {
        Task<uint> GetConsumedAsync(DepositDetails deposit);
    }
}
