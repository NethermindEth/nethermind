using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Queries;
using Nethermind.DataMarketplace.Providers.Repositories;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Persistence.Rocks.Repositories
{
    internal class ProviderDepositApprovalRocksRepository : IProviderDepositApprovalRepository
    {
        private readonly IDb _database;
        private readonly IRlpDecoder<DepositApproval> _rlpDecoder;
        private  IRlpStreamDecoder<DepositApproval> RlpStreamDecoder => (IRlpStreamDecoder<DepositApproval>)_rlpDecoder;
        private  IRlpObjectDecoder<DepositApproval> RlpObjectDecoder => (IRlpObjectDecoder<DepositApproval>)_rlpDecoder;

        public ProviderDepositApprovalRocksRepository(IDb database, IRlpNdmDecoder<DepositApproval> rlpDecoder)
        {
            _database = database;
            _rlpDecoder = rlpDecoder ?? throw new ArgumentNullException(nameof(rlpDecoder));
        }

        public Task<DepositApproval?> GetAsync(Keccak id)
        {
            byte[] fromDb = _database.Get(id);
            return fromDb == null ? Task.FromResult<DepositApproval?>(null) : Task.FromResult<DepositApproval?>(Decode(fromDb));
        }

        public Task<PagedResult<DepositApproval>> BrowseAsync(GetProviderDepositApprovals query)
        {
            if (query is null)
            {
                return Task.FromResult(PagedResult<DepositApproval>.Empty);
            }

            var depositApprovalsBytes = _database.GetAllValues().ToArray();
            if (depositApprovalsBytes.Length == 0)
            {
                return Task.FromResult(PagedResult<DepositApproval>.Empty);
            }

            var depositApprovals = new DepositApproval[depositApprovalsBytes.Length];
            for (var i = 0; i < depositApprovalsBytes.Length; i++)
            {
                depositApprovals[i] = Decode(depositApprovalsBytes[i]);
            }

            var filteredDepositApprovals = depositApprovals.AsEnumerable();
            if (!(query.DataAssetId is null))
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.AssetId == query.DataAssetId);
            }

            if (!(query.Consumer is null))
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.Consumer == query.Consumer);
            }

            if (query.OnlyPending)
            {
                filteredDepositApprovals = filteredDepositApprovals.Where(a => a.State == DepositApprovalState.Pending);
            }

            return Task.FromResult(filteredDepositApprovals.OrderByDescending(a => a.Timestamp).ToArray().Paginate(query));
        }

        public Task AddAsync(DepositApproval depositApproval) => AddOrUpdateAsync(depositApproval);
        public Task UpdateAsync(DepositApproval depositApproval) => AddOrUpdateAsync(depositApproval);

        private Task AddOrUpdateAsync(DepositApproval depositApproval)
        {
            var rlp = RlpObjectDecoder.Encode(depositApproval);
            _database.Set(depositApproval.Id, rlp.Bytes);

            return Task.CompletedTask;
        }

        private DepositApproval Decode(byte[] bytes)
            => RlpStreamDecoder.Decode(bytes.AsRlpStream());
    }
}