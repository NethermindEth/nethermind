using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.AuRa.Contracts
{
    public class ValidatorContract : SystemContract
    {
        private readonly IAbiEncoder _abiEncoder;
        private readonly byte[] _finalizeChangeTransactionData;
        private readonly byte[] _getValidatorsTransactionData;
        
        private static readonly IEqualityComparer<LogEntry> LogEntryEqualityComparer = new LogEntryAddressAndTopicEqualityComparer();

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        private static class Definition
        {
            /// <summary>
            /// Called when an initiated change reaches finality and is activated.
            /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
            ///
            /// Also called when the contract is first enabled for consensus. In this case,
            /// the "change" finalized is the activation of the initial set.
            /// function finalizeChange();
            /// </summary>
            public static readonly AbiEncodingInfo finalizeChange =
                new AbiEncodingInfo(AbiEncodingStyle.IncludeSignature, new AbiSignature(nameof(finalizeChange)));

            /// <summary>
            /// Issue this log event to signal a desired change in validator set.
            /// This will not lead to a change in active validator set until
            /// finalizeChange is called.
            ///
            /// Only the last log event of any block can take effect.
            /// If a signal is issued while another is being finalized it may never
            /// take effect.
            ///
            /// _parent_hash here should be the parent block hash, or the
            /// signal will not be recognized.
            /// event InitiateChange(bytes32 indexed _parent_hash, address[] _new_set);
            /// </summary>
            private const string initializeChangeEventSignature = "InitiateChange(bytes32,address[])";
            public static Keccak initializeChangeEventHash = Keccak.Compute(initializeChangeEventSignature);

            /// <summary>
            /// Get current validator set (last enacted or initial if no changes ever made)
            /// function getValidators() constant returns (address[] _validators);
            /// </summary>
            public static readonly AbiEncodingInfo getValidators =
                new AbiEncodingInfo(AbiEncodingStyle.IncludeSignature, new AbiSignature(nameof(getValidators)));

            public static readonly AbiEncodingInfo addressArrayResult = new AbiEncodingInfo(AbiEncodingStyle.None,
                new AbiSignature(nameof(addressArrayResult), new AbiArray(AbiType.Address)));
        }

        public ValidatorContract(IAbiEncoder abiEncoder)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _finalizeChangeTransactionData = _abiEncoder.Encode(Definition.finalizeChange);
            _getValidatorsTransactionData = _abiEncoder.Encode(Definition.getValidators);
        }

        public Transaction FinalizeChange(Address contractAddress, Block block)
            => GenerateTransaction(contractAddress,
                _finalizeChangeTransactionData, 
                block.GasLimit - block.GasUsed, 
                UInt256.Zero);

        public Transaction GetValidators(Address contractAddress, Block block)
            => GenerateTransaction(contractAddress, 
                _getValidatorsTransactionData,
                block.GasLimit - block.GasUsed, 
                UInt256.Zero);

        public bool CheckInitiateChangeEvent(Address contractAddress, Block block, TxReceipt[] receipts, out Address[] addresses)
        {
            var logEntry = new LogEntry(contractAddress, 
                Array.Empty<byte>(),
                new[] {Definition.initializeChangeEventHash, block.ParentHash});

            if (block.Bloom.IsMatch(logEntry))
            {
                 // iterating backwards, we are interested only in the last one
                for (int i = receipts.Length - 1; i >= 0; i--)
                {
                    var receipt = receipts[i];
                    if (receipt.Bloom.IsMatch(logEntry))
                    {
                        for (int j = receipt.Logs.Length - 1; j >= 0; j--)
                        {
                            var receiptLog = receipt.Logs[j];
                            if (LogEntryEqualityComparer.Equals(logEntry, receiptLog))
                            {
                                addresses = DecodeAddresses(receiptLog.Data);
                                return true;                                
                            }
                        }
                    }
                }
            }

            addresses = null;
            return false;
        }

        public Address[] DecodeAddresses(byte[] data)
        {
            var objects = _abiEncoder.Decode(Definition.addressArrayResult, data);
            return (Address[]) objects[0];
        }
    }

    public class LogEntryAddressAndTopicEqualityComparer : IEqualityComparer<LogEntry>
    {
        public bool Equals(LogEntry x, LogEntry y)
        {
            return ReferenceEquals(x, y) || (x != null && x.LoggersAddress == y?.LoggersAddress && x.Topics.SequenceEqual(y.Topics));
        }

        public int GetHashCode(LogEntry obj)
        {
            return obj.Topics.Aggregate(obj.LoggersAddress.GetHashCode(), (i, keccak) => i ^ keccak.GetHashCode());
        }
    }
}