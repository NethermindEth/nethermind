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

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus
{
    public class SealEngine : ISealEngine
    {
        private readonly ISealer _sealer;
        private readonly ISealValidator _sealValidator;

        public SealEngine(ISealer? sealer, ISealValidator? sealValidator)
        {
            _sealer = sealer ?? throw new ArgumentNullException(nameof(sealer));
            _sealValidator = sealValidator ?? throw new ArgumentNullException(nameof(sealValidator));
        }

        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken) => 
            _sealer.SealBlock(block, cancellationToken);

        public bool CanSeal(long blockNumber, Keccak parentHash) => 
            _sealer.CanSeal(blockNumber, parentHash);

        public Address Address => _sealer.Address;
        
        public bool ValidateParams(BlockHeader parent, BlockHeader header) => 
            _sealValidator.ValidateParams(parent, header);

        public bool ValidateSeal(BlockHeader header, bool force) => 
            _sealValidator.ValidateSeal(header, force);
    }
}
