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
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
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
    public class AuRaAdditionalBlockProcessorFactory : IAuRaAdditionalBlockProcessorFactory
    {
        private const long DefaultStartBlockNumber = 0;
        
        private readonly IStateProvider _stateProvider;
        private readonly IAbiEncoder _abiEncoder;
        private readonly IDb _stateDb;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IBlockTree _blockTree;
        private readonly ILogManager _logManager;

        public AuRaAdditionalBlockProcessorFactory(
            IDb stateDb,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            ILogManager logManager)
        {
            _stateProvider = stateProvider;
            _abiEncoder = abiEncoder;
            _stateDb = stateDb;
            _transactionProcessor = transactionProcessor;
            _blockTree = blockTree;
            _logManager = logManager;
        }

        public IAuRaValidatorProcessor CreateValidatorProcessor(AuRaParameters.Validator validator, long? startBlock = null)
        {
            long startBlockNumber = startBlock ?? DefaultStartBlockNumber;
            switch (validator.ValidatorType)
            {
                case AuRaParameters.ValidatorType.List:
                    return new ListValidator(validator);
                case AuRaParameters.ValidatorType.Contract:
                    return new ContractValidator(validator, _stateDb, _stateProvider, _abiEncoder, _transactionProcessor, _blockTree, _logManager, startBlockNumber);
                case AuRaParameters.ValidatorType.ReportingContract:
                    return new ReportingContractValidator(validator, _stateDb, _stateProvider, _abiEncoder, _transactionProcessor, _blockTree, _logManager, startBlockNumber);
                case AuRaParameters.ValidatorType.Multi:
                    return new MultiValidator(validator, this, _logManager);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}