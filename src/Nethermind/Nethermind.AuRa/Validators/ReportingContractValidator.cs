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

using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa.Validators
{
    public class ReportingContractValidator : ContractValidator
    {
        public ReportingContractValidator(AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            ILogManager logManager,
            long startBlockNumber) 
            : base(validator, stateProvider, abiEncoder, transactionProcessor, blockTree, receiptStorage, validatorStore, validSealerStrategy, logManager, startBlockNumber)
        {
        }
    }
}