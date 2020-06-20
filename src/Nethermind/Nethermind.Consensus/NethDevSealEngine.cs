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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Consensus
{
    public class NethDevSealEngine : ISealer, ISealValidator
    {
        private NethDevSealEngine(Address address = null)
        {
            Address = address ?? Address.Zero;
        }

        public static NethDevSealEngine Instance { get; } = new NethDevSealEngine();

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            block.Header.MixHash = Keccak.Zero;
            block.Header.Hash = block.CalculateHash();
            return Task.FromResult(block);
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            return true;
        }

        public Address Address { get; }

        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            return true;
        }
    }
}
