// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories
{
    public class EthRequestRocksRepository : IEthRequestRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<EthRequest> _rlpDecoder;

        public EthRequestRocksRepository(IDb database, IRlpNdmDecoder<EthRequest> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<EthRequest?> GetLatestAsync(string host)
        {
            var requestsBytes = _database.GetAllValues().ToArray();
            if (requestsBytes.Length == 0)
            {
                return Task.FromResult<EthRequest?>(null);
            }

            var requests = new EthRequest[requestsBytes.Length];
            for (var i = 0; i < requestsBytes.Length; i++)
            {
                requests[i] = Decode(requestsBytes[i]);
            }

            return Task.FromResult<EthRequest?>(requests.FirstOrDefault(r => r.Host == host));
        }

        public Task AddAsync(EthRequest request) => AddOrUpdateAsync(request);

        public Task UpdateAsync(EthRequest request) => AddOrUpdateAsync(request);

        public Task<UInt256> SumDailyRequestsTotalValueAsync(DateTime date)
        {
            var requestsBytes = _database.GetAllValues().ToArray();
            if (requestsBytes.Length == 0)
            {
                return Task.FromResult<UInt256>(0);
            }

            var requests = new EthRequest[requestsBytes.Length];
            for (var i = 0; i < requestsBytes.Length; i++)
            {
                requests[i] = Decode(requestsBytes[i]);
            }

            var totalValue = UInt256.Zero;
            totalValue = requests.Where(r => r.RequestedAt.Date == date.Date)
                .Aggregate(totalValue, (current, request) => current + request.Value);

            return Task.FromResult(totalValue);
        }

        private Task AddOrUpdateAsync(EthRequest request)
        {
            var rlp = _rlpDecoder.Encode(request);
            _database.Set(request.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private EthRequest Decode(byte[] bytes)
            => _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
