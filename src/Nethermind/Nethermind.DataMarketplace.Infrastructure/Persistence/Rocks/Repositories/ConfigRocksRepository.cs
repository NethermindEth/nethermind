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

using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Persistence.Rocks.Repositories
{
    public class ConfigRocksRepository : IConfigRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<NdmConfig> _rlpDecoder;

        public ConfigRocksRepository(IDb database, IRlpNdmDecoder<NdmConfig> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public Task<NdmConfig?> GetAsync(string id) => Task.FromResult(Decode(_database.Get(Keccak.Compute(id))));

        public Task AddAsync(NdmConfig config) => AddOrUpdateAsync(config);

        public Task UpdateAsync(NdmConfig config) => AddOrUpdateAsync(config);

        private Task AddOrUpdateAsync(NdmConfig config)
        {
            Serialization.Rlp.Rlp rlp = _rlpDecoder.Encode(config);
            _database.Set(Keccak.Compute(config.Id), rlp.Bytes);

            return Task.CompletedTask;
        }

        private NdmConfig? Decode(byte[] bytes)
            => bytes is null
                ? null
                : _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
