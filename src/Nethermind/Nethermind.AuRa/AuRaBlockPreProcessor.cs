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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.Store;

namespace Nethermind.AuRa
{
    public class AuRaBlockPreProcessor : IBlockPreProcessor
    {
        public static readonly Address SystemUser = new Address("0xfffffffffffffffffffffffffffffffffffffffe");
        
        private readonly IStateProvider _stateProvider;
        private readonly IAbiEncoder _abiEncoder;
        private readonly AuRaParameters _auRaParameters;

        public AuRaBlockPreProcessor(IStateProvider stateProvider, IAbiEncoder abiEncoder, AuRaParameters auRaParameters)
        {
            _stateProvider = stateProvider?? throw new ArgumentNullException(nameof(stateProvider));;
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _auRaParameters = auRaParameters ?? throw new ArgumentNullException(nameof(auRaParameters));;
        }
        
        public Transaction[] InjectTransactions(Block block)
        {
            if (block.Number == 1)
            {
                // _stateProvider.CreateAccount(SystemUser, 0);
            }
            
            var txData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, ValidatorContractData.finalizeChange);
            var contractAddress = _auRaParameters.Validators.Last(b => b.Key < block.Number).Value;

            var transaction = new Transaction
            {
                Value = 0,
                Data = txData,
                To = contractAddress,
                SenderAddress = SystemUser,
                GasLimit = block.GasLimit - block.GasUsed,
                GasPrice = 0.GWei(),
                Nonce = _stateProvider.GetNonce(SystemUser),
            };

            transaction.Hash = Transaction.CalculateHash(transaction);
            
            return new[] { transaction };
        }
    }

    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public static class ValidatorContractData
    {
        /// Called when an initiated change reaches finality and is activated.
        /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
        ///
        /// Also called when the contract is first enabled for consensus. In this case,
        /// the "change" finalized is the activation of the initial set.
        public static AbiSignature finalizeChange = new AbiSignature(nameof(finalizeChange));
    }
}