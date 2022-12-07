// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Queries
{
    public class GetDeposits : PagedQueryBase
    {
        public bool OnlyUnconfirmed { get; set; }
        public bool OnlyNotRejected { get; set; }
        public bool OnlyPending { get; set; }
        public bool EligibleToRefund { get; set; }
        public long CurrentBlockTimestamp { get; set; }
    }
}
