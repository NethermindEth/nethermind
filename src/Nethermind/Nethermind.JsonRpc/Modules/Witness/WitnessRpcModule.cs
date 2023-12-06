// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;

namespace Nethermind.JsonRpc.Modules.Witness
{
    public class WitnessRpcModule : IWitnessRpcModule
    {
        private readonly IBlockFinder _blockFinder;
        private readonly IWitnessRepository _witnessRepository;

        public WitnessRpcModule(IWitnessRepository witnessRepository, IBlockFinder finder)
        {
            _witnessRepository = witnessRepository;
            _blockFinder = finder;
        }

        public Task<ResultWrapper<Hash256[]>> get_witnesses(BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.Object is null)
            {
                return Task.FromResult(ResultWrapper<Hash256[]>.Fail("Block not found", ErrorCodes.ResourceNotFound));
            }

            Hash256 hash = searchResult.Object.Hash;
            Hash256[] result = _witnessRepository.Load(hash!);
            return result is null ? Task.FromResult(ResultWrapper<Hash256[]>.Fail("Witness unavailable", ErrorCodes.ResourceUnavailable)) : Task.FromResult(ResultWrapper<Hash256[]>.Success(result));
        }
    }
}
