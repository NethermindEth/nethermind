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
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json.Abi;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class ValidatorContract : SystemContract
    {
        private readonly IAbiEncoder _abiEncoder;
        private readonly byte[] _finalizeChangeTransactionData;
        private readonly byte[] _getValidatorsTransactionData;
        
        private static readonly IEqualityComparer<LogEntry> LogEntryEqualityComparer = new LogEntryAddressAndTopicEqualityComparer();
        
        public static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<ValidatorContract>();
        
        /// <summary>
        /// Called when an initiated change reaches finality and is activated.
        /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
        ///
        /// Also called when the contract is first enabled for consensus. In this case,
        /// the "change" finalized is the activation of the initial set.
        /// function finalizeChange();
        /// </summary>
        public const string FinalizeChangeFunction = "finalizeChange";
        
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
        public  const string InitiateChangeEvent = "InitiateChange";
        
        /// <summary>
        /// Get current validator set (last enacted or initial if no changes ever made)
        /// function getValidators() constant returns (address[] _validators);
        /// </summary>
        public  const string GetValidatorsFunction = "getValidators";

        public ValidatorContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress) : base(transactionProcessor, abiEncoder, contractAddress)
        {
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
            _finalizeChangeTransactionData = _abiEncoder.Encode(Definition.Functions[FinalizeChangeFunction].GetCallInfo());
            _getValidatorsTransactionData = _abiEncoder.Encode(Definition.Functions[GetValidatorsFunction].GetCallInfo());
        }

        public void FinalizeChange(BlockHeader blockHeader) => TryInvokeTransaction(blockHeader, GenerateSystemTransaction(_finalizeChangeTransactionData), new CallOutputTracer());

        public Address[] GetValidators(BlockHeader blockHeader)
        {
            CallOutputTracer tracer = new CallOutputTracer();
            var transaction = GenerateTransaction(_getValidatorsTransactionData, ContractAddress);
            InvokeTransaction(blockHeader, transaction, tracer);
            var data = tracer.ReturnValue;
            return data.Length == 0 ? Array.Empty<Address>() : DecodeAddresses(data);
        }

        public bool CheckInitiateChangeEvent(Address contractAddress, BlockHeader blockHeader, TxReceipt[] receipts, out Address[] addresses)
        {
            var logEntry = new LogEntry(contractAddress, 
                Array.Empty<byte>(),
                new[] {Definition.Events[InitiateChangeEvent].GetHash(), blockHeader.ParentHash});

            if (blockHeader.TryFindLog(receipts, logEntry, LogEntryEqualityComparer, out var foundEntry))
            {
                addresses = DecodeAddresses(foundEntry.Data);
                return true;                
            }

            addresses = null;
            return false;
        }

        public Address[] DecodeAddresses(byte[] data)
        {
            var objects = _abiEncoder.Decode(Definition.Functions[GetValidatorsFunction].GetReturnInfo(), data);
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