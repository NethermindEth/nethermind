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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public sealed class ListValidator : AuRaValidatorProcessorBase
    {
        public ListValidator(AuRaParameters.Validator validator, IValidSealerStrategy validSealerStrategy, ILogManager logManager) : base(validator, validSealerStrategy, logManager)
        {
            Validators = validator.Addresses?.Length > 0
                ? validator.Addresses
                : throw new ArgumentException("Empty validator Addresses.", nameof(validator.Addresses));
        }
    }
}