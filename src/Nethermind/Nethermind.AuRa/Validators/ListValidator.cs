/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Evm;

namespace Nethermind.AuRa.Validators
{
    public class ListValidator : IAuRaValidatorProcessor
    {
        private readonly Address[] _validatorAddresses;

        public ListValidator(AuRaParameters.Validator validator)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            if (validator.ValidatorType != AuRaParameters.ValidatorType.List) 
                throw new ArgumentException("Wrong validator type.", nameof(validator));
            
            _validatorAddresses = validator.Addresses?.Length > 0
                ? validator.Addresses
                : throw new ArgumentException("Empty validator Addresses.", nameof(validator.Addresses));
        }
        
        public void PreProcess(Block block) { }

        public void PostProcess(Block block, TxReceipt[] receipts) { }

        public void Initialize(Block block, TransactionProcessor transactionProcessor) { }

        public bool IsValidSealer(Address address, long step) => _validatorAddresses.GetItemRoundRobin(step) == address;
        public int MinSealersForFinalization => _validatorAddresses.MinSealersForFinalization();
        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager) { } // ListValidator doesn't change its behaviour/state based on Finalization of blocks, only Multi and Contract validators do.

        public AuRaParameters.ValidatorType Type => AuRaParameters.ValidatorType.List;
    }
}