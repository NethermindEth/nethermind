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
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaHeaderValidator : HeaderValidator
    {
        private readonly IList<long> _blockGasLimitContractTransitions;

        public AuRaHeaderValidator(
            IBlockTree blockTree, 
            ISealValidator sealValidator, 
            ISpecProvider specProvider, 
            ILogManager logManager,
            IList<long> blockGasLimitContractTransitions)
            : base(blockTree, sealValidator, specProvider, logManager)
        {
            _blockGasLimitContractTransitions = blockGasLimitContractTransitions ?? throw new ArgumentNullException(nameof(blockGasLimitContractTransitions));
        }

        protected override bool ValidateGasLimitRange(BlockHeader header, BlockHeader parent, IReleaseSpec spec) => 
            _blockGasLimitContractTransitions.TryGetForBlock(header.Number, out _) || base.ValidateGasLimitRange(header, parent, spec);
    }
}
