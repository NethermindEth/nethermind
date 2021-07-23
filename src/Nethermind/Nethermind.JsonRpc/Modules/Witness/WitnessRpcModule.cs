//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

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

        public async Task<ResultWrapper<Keccak[]>> get_witnesses(string blockHash)
        {
            var blockParameter = new BlockParameter(new Keccak(blockHash));
            Block block = _blockFinder.FindBlock(blockParameter);
            Keccak[] result = _witnessRepository.Load(block.Hash);
            return ResultWrapper<Keccak[]>.Success(result);
        }
    }
}
