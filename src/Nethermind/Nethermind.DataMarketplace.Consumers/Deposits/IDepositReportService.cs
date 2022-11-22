// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Queries;

namespace Nethermind.DataMarketplace.Consumers.Deposits
{
    public interface IDepositReportService
    {
        Task<DepositsReport> GetAsync(GetDepositsReport query);
    }
}
