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
using System.Linq;
using Nethermind.Abi;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Executor
{
    public class UserOperationTxBuilder : IUserOperationTxBuilder
    {
        private readonly AbiDefinition _entryPointContractAbi;
        private readonly ISigner _signer;
        private readonly Address _entryPointContractAddress;
        private readonly ISpecProvider _specProvider;
        private readonly IAbiEncoder _abiEncoder;

        public UserOperationTxBuilder(
            AbiDefinition entryPointContractAbi, 
            ISigner signer, 
            Address entryPointContractAddress,
            ISpecProvider specProvider)
        {
            _entryPointContractAbi = entryPointContractAbi;
            _signer = signer;
            _entryPointContractAddress = entryPointContractAddress;
            _specProvider = specProvider;

            _abiEncoder = new AbiEncoder();
        }

        public Transaction BuildTransaction(long gaslimit, byte[] callData, Address sender, BlockHeader parent, IReleaseSpec spec,
            UInt256 nonce, bool systemTransaction)
        {
            Transaction transaction = systemTransaction ? new SystemTransaction() : new Transaction();

            UInt256 fee = BaseFeeCalculator.Calculate(parent, spec);

            transaction.GasPrice = fee;
            transaction.GasLimit = gaslimit;
            transaction.To = _entryPointContractAddress;
            transaction.ChainId = _specProvider.ChainId;
            transaction.Nonce = nonce;
            transaction.Value = 0;
            transaction.Data = callData;
            transaction.Type = TxType.EIP1559;
            transaction.DecodedMaxFeePerGas = fee;
            transaction.SenderAddress = sender;

            if (!systemTransaction) _signer.Sign(transaction);
            transaction.Hash = transaction.CalculateHash();

            return transaction;
        }

        public Transaction BuildTransactionFromUserOperations(
            IEnumerable<UserOperation> userOperations, 
            BlockHeader parent, 
            long gasLimit,
            UInt256 nonce,
            IReleaseSpec spec)
        {
            byte[] computedCallData;

            // use handleOp is only one op is used, handleOps if multiple
            UserOperation[] userOperationArray = userOperations.ToArray();
            if (userOperationArray.Length == 1)
            {
                UserOperation userOperation = userOperationArray[0];

                AbiSignature abiSignature = _entryPointContractAbi.Functions["handleOp"].GetCallInfo().Signature;
                computedCallData = _abiEncoder.Encode(
                    AbiEncodingStyle.IncludeSignature,
                    abiSignature,
                    userOperation.Abi, _signer.Address);
            }
            else
            {
                AbiSignature abiSignature = _entryPointContractAbi.Functions["handleOps"].GetCallInfo().Signature;
                computedCallData = _abiEncoder.Encode(
                    AbiEncodingStyle.IncludeSignature,
                    abiSignature,
                    userOperationArray.Select(op => op.Abi).ToArray(), _signer.Address);
            }

            Transaction transaction =
                BuildTransaction(gasLimit, computedCallData, _signer.Address, parent, spec, nonce, false);

            return transaction;
        }

        public FailedOp? DecodeEntryPointOutputError(byte[] output)
        {
            try
            {
                // the failedOp error in the entrypoint provides useful error messages, use if possible
                object[] decoded = _abiEncoder.Decode(AbiEncodingStyle.IncludeSignature,
                    _entryPointContractAbi.Errors["FailedOp"].GetCallInfo().Signature, output);
                FailedOp failedOp = new((UInt256)decoded[0], (Address)decoded[1], (string)decoded[2]);
                return failedOp;
            }
            catch (Exception)
            {
                return null;
            }
            
        }
    }
}
