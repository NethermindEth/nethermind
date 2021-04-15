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

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers
{
    public class Eth2SealEngine : ISealer, ISealValidator
    {
        private readonly ISigner _signer;

        public Eth2SealEngine(ISigner signer)
        {
            _signer = signer;
        }
            
        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken) => Task.FromResult(block);

        public bool CanSeal(long blockNumber, Keccak parentHash) => true;

        public Address Address => _signer.Address;
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header) => true;

        public bool ValidateSeal(BlockHeader header, bool force) => true;
    }
}
