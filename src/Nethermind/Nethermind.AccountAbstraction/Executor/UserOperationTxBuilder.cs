// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public Transaction BuildTransaction(long gaslimit, byte[] callData, Address sender, BlockHeader parent, IEip1559Spec specFor1559,
            UInt256 nonce, bool systemTransaction)
        {
            Transaction transaction = systemTransaction ? new SystemTransaction() : new Transaction();

            UInt256 fee = BaseFeeCalculator.Calculate(parent, specFor1559);

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
            IEip1559Spec specFor1559)
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
                BuildTransaction(gasLimit, computedCallData, _signer.Address, parent, specFor1559, nonce, false);

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
