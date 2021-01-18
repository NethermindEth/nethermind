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

using System;
using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.Validators
{
    public sealed class ListBasedValidator : AuRaValidatorBase
    {
        public ListBasedValidator(AuRaParameters.Validator validator, IValidSealerStrategy validSealerStrategy, IValidatorStore validatorStore, ILogManager logManager, long startBlockNumber, bool forSealing = false) 
            : base(validSealerStrategy, validatorStore, logManager, startBlockNumber, forSealing)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            
            Validators = validator.Addresses?.Length > 0
                ? validator.Addresses
                : throw new ArgumentException("Empty validator Addresses.", nameof(validator.Addresses));

            InitValidatorStore();
        }
    }
}
