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
using Nethermind.Abi;
using Nethermind.AuRa.Contracts;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaBlockPreProcessor : IBlockPreProcessor
    {
        private readonly IStateProvider _stateProvider;
        private readonly ValidatorContract _validatorContract;
        private bool _finalizeChangeCalled = false;

        public AuRaBlockPreProcessor(
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            AuRaParameters auRaParameters)
        {
            _stateProvider = stateProvider?? throw new ArgumentNullException(nameof(stateProvider));;
            _validatorContract = new ValidatorContract(abiEncoder, auRaParameters);
        }
        
        public Transaction[] InjectTransactions(Block block)
        {
            if (block.Number == 1)
            {
                CreateSystemAccount();

            }

            // if (!_finalizeChangeCalled)
            {
                var finalizeChangeTransaction = _validatorContract.FinalizeChange(block, _stateProvider);
                if (finalizeChangeTransaction != null)
                {
                    _finalizeChangeCalled = true;
                    return new[] {finalizeChangeTransaction};
                }
            }

            return Array.Empty<Transaction>();
        }

        private void CreateSystemAccount()
        {
            _stateProvider.CreateAccount(Address.SystemUser, UInt256.Zero);
            _stateProvider.Commit(Homestead.Instance);
        }
    }
}