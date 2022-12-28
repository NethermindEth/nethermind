// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
