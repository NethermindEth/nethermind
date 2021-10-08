//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
                return (uint) _timestamper.UnixTime.Seconds - deposit.ConfirmationTimestamp;
            }

            var sessions = await _sessionRepository.BrowseAsync(new GetConsumerSessions
            {
                DepositId = deposit.Id,
                Results = int.MaxValue
            });

            return sessions.Items.Any() ? (uint) sessions.Items.Sum(s => s.ConsumedUnits) : 0;
        }
    }
}
