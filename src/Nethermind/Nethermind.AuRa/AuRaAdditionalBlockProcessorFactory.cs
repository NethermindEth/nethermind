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
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade;
using Nethermind.HashLib.Crypto.SHA3;
using Nethermind.Logging;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaProcessorFactory : IAuRaAdditionalBlockProcessorFactory
    {
        private const long DefaultStartBlockNumber = 1;
        
        private readonly IStateProvider _stateProvider;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IDb _stateDb;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptStorage _receiptStorage;
        private readonly ILogManager _logManager;

        public AuRaProcessorFactory(
            IDb stateDb,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            IReceiptStorage receiptStorage,
            ILogManager logManager)
        {
            _stateProvider = stateProvider;
            _abiEncoder = abiEncoder;
            _stateDb = stateDb;
            _transactionProcessor = transactionProcessor;
            _blockTree = blockTree;
            _receiptStorage = receiptStorage;
            _logManager = logManager;
        }

        public IAuRaValidatorProcessor CreateValidatorProcessor(AuRaParameters.Validator validator, long? startBlock = null)
        {
            long startBlockNumber = startBlock ?? DefaultStartBlockNumber;
            return validator.ValidatorType switch
            {
                AuRaParameters.ValidatorType.List => (IAuRaValidatorProcessor) new ListValidator(validator, _logManager),
                AuRaParameters.ValidatorType.Contract => new ContractValidator(validator, _stateDb, _stateProvider, _abiEncoder, _transactionProcessor, _blockTree, _receiptStorage, _logManager, startBlockNumber),
                AuRaParameters.ValidatorType.ReportingContract => new ReportingContractValidator(validator, _stateDb, _stateProvider, _abiEncoder, _transactionProcessor, _blockTree, _receiptStorage, _logManager, startBlockNumber),
                AuRaParameters.ValidatorType.Multi => new MultiValidator(validator, this, _blockTree, _logManager),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}