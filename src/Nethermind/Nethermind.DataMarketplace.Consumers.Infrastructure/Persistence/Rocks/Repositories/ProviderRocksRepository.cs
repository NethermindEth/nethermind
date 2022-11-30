// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.DataAssets.Domain;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Consumers.Providers.Domain;
using Nethermind.DataMarketplace.Consumers.Providers.Repositories;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks.Repositories
{
    public class ProviderRocksRepository : IProviderRepository
    {
        private readonly IDb _database;
        private readonly IRlpNdmDecoder<DepositDetails> _rlpDecoder;

        public ProviderRocksRepository(IDb database, IRlpNdmDecoder<DepositDetails> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder;
        }

        public async Task<IReadOnlyList<DataAssetInfo>> GetDataAssetsAsync()
            => await Task.FromResult(GetAll()
                .Select(d => new DataAssetInfo(d.DataAsset.Id, d.DataAsset.Name, d.DataAsset.Description))
                .Distinct()
                .ToList());

        public async Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync()
            => await Task.FromResult(GetAll()
                .Select(d => new ProviderInfo(d.DataAsset.Provider.Name, d.DataAsset.Provider.Address))
                .Distinct()
                .ToList());

        private IEnumerable<DepositDetails> GetAll()
        {
            byte[][] depositsBytes = _database.GetAllValues().ToArray();
            if (depositsBytes.Length == 0)
            {
                yield break;
            }

            DepositDetails[] dataAssets = new DepositDetails[depositsBytes.Length];
            for (int i = 0; i < depositsBytes.Length; i++)
            {
                yield return dataAssets[i] = Decode(depositsBytes[i]);
            }
        }

        private DepositDetails Decode(byte[] bytes)
            => _rlpDecoder.Decode(bytes.AsRlpStream());
    }
}
