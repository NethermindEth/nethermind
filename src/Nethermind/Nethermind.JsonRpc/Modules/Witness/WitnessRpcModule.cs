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

        public Task<ResultWrapper<Keccak[]>> get_witnesses(BlockParameter blockParameter)
        {
            SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
            if (searchResult.Object is null)
            {
                return Task.FromResult(ResultWrapper<Keccak[]>.Fail("Block not found", ErrorCodes.ResourceNotFound));
            }

            Keccak hash = searchResult.Object.Hash;
            Keccak[] result = _witnessRepository.Load(hash!);
            return result is null ? Task.FromResult(ResultWrapper<Keccak[]>.Fail("Witness unavailable", ErrorCodes.ResourceUnavailable)) : Task.FromResult(ResultWrapper<Keccak[]>.Success(result));
        }
    }
}
