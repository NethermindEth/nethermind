// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Sessions.Queries;
using Nethermind.DataMarketplace.Consumers.Sessions.Repositories;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Deposits.Services
{
    public class DepositUnitsCalculator : IDepositUnitsCalculator
    {
        private readonly IConsumerSessionRepository _sessionRepository;
        private readonly ITimestamper _timestamper;

        public DepositUnitsCalculator(IConsumerSessionRepository sessionRepository, ITimestamper timestamper)
        {
            _sessionRepository = sessionRepository;
            _timestamper = timestamper;
        }

        public async Task<uint> GetConsumedAsync(DepositDetails deposit)
        {
            if (!deposit.Confirmed)
            {
                return 0;
            }

            if (deposit.DataAsset.UnitType == DataAssetUnitType.Time)
            {
                return (uint)_timestamper.UnixTime.Seconds - deposit.ConfirmationTimestamp;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = deposit.Id,
                Results = int.MaxValue
            });

            return sessions.Items.Any() ? (uint)sessions.Items.Sum(s => s.ConsumedUnits) : 0;
        }
    }
}
