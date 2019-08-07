using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.AuRa.Contracts
{
    public class ValidatorContract
    {
        private readonly IAbiEncoder _abiEncoder;
        private readonly byte[] _finalizeChangeTransactionData;
        private Address _currentAddress = null;
        private int _nextValidator = 0;
        private readonly KeyValuePair<long, Address>[] _validators;
        

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        private static class Definition
        {
            /// Called when an initiated change reaches finality and is activated.
            /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
            ///
            /// Also called when the contract is first enabled for consensus. In this case,
            /// the "change" finalized is the activation of the initial set.
            public static readonly AbiSignature finalizeChange = new AbiSignature(nameof(finalizeChange));
        }

        public ValidatorContract(IAbiEncoder abiEncoder, AuRaParameters auRaParameters)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _validators = auRaParameters?.Validators.OrderBy(v => v.Key).ToArray() ?? throw new ArgumentNullException(nameof(auRaParameters));
            
            GetContractAddress(0);
            _abiEncoder = abiEncoder;
            _finalizeChangeTransactionData = _abiEncoder.Encode(AbiEncodingStyle.IncludeSignature, Definition.finalizeChange);
        }

        public Transaction FinalizeChange(Block block, IStateProvider stateProvider)
        {
            return GenerateTransaction(_finalizeChangeTransactionData, block.Number, block.GasLimit - block.GasUsed, stateProvider.GetNonce(Address.SystemUser));
        }

        private Transaction GenerateTransaction(byte[] transactionData, long blockNumber, long gasLimit, UInt256 nonce)
        {
            var contractAddress = GetContractAddress(blockNumber);
            
            if (contractAddress != null)
            {
                var transaction = new Transaction
                {
                    Value = 0,
                    Data = transactionData,
                    To = contractAddress,
                    SenderAddress = Address.SystemUser,
                    GasLimit = gasLimit,
                    GasPrice = 0.GWei(),
                    Nonce = nonce,
                };
                
                transaction.Hash = Transaction.CalculateHash(transaction);

                return transaction;
            }

            return null;
        }

        private Address GetContractAddress(long blockNumber)
        {
            while (_validators.Length > _nextValidator && blockNumber >= _validators[_nextValidator].Key)
            {
                _currentAddress = _validators[_nextValidator].Value;
                _nextValidator++;
            }

            return _currentAddress;
        }
    }
}