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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.TxPool;
using DestinationTuple = System.ValueTuple<Nethermind.Core.Address, byte[], Nethermind.Int256.UInt256>;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>
    /// Permission contract for <see cref="ITxPool"/> transaction ordering
    /// <seealso cref="https://github.com/poanetwork/posdao-contracts/blob/master/contracts/TxPriority.sol"/> 
    /// </summary>
    public class TxPriorityContract : Contract
    {
        private ConstantContract Constant { get; }
        
        public TxPriorityContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) 
            : base(abiEncoder, contractAddress)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
            SendersWhitelist = new DataContract<Address>(GetSendersWhitelist, SendersWhitelistSet, false);
            MinGasPrices = new DataContract<Destination>(GetMinGasPrices, MinGasPriceSet, true);
            Priorities = new DataContract<Destination>(GetPriorities, PrioritySet, true);
        }

        public Address[] GetSendersWhitelist(BlockHeader parentHeader) => Constant.Call<Address[]>(parentHeader, nameof(GetSendersWhitelist), ContractAddress);

        public Destination[] GetMinGasPrices(BlockHeader parentHeader) => Constant.Call<DestinationTuple[]>(parentHeader, nameof(GetMinGasPrices), ContractAddress)
            .Select(x => (Destination)x).ToArray();
        
        public Destination[] GetPriorities(BlockHeader parentHeader) => Constant.Call<DestinationTuple[]>(parentHeader, nameof(GetPriorities), ContractAddress)
            .Select(x => (Destination)x).ToArray();
        
        public IEnumerable<Destination> PrioritySet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(PrioritySet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log.Data);
            }
        }
        
        public IEnumerable<Destination> MinGasPriceSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(MinGasPriceSet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log.Data);
            }
        }
        
        public IEnumerable<Address> SendersWhitelistSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(MinGasPriceSet));

            return blockHeader.TryFindLog(receipts, logEntry, LogEntryAddressAndTopicEqualityComparer.Instance, out LogEntry foundEntry) 
                ? DecodeAddresses(foundEntry.Data) 
                : Array.Empty<Address>();
        }
        
        public Address[] DecodeAddresses(byte[] data)
        {
            var objects = DecodeReturnData(nameof(GetSendersWhitelist), data);
            return (Address[]) objects[0];
        }

        private Destination DecodeDestination(byte[] data)
        {
            return default;
            // DecodeReturnData()
        }

        public readonly struct Destination
        {
            public Destination(Address target, byte[] fnSignature, UInt256 value)
            {
                Target = target;
                FnSignature = fnSignature;
                Value = value;
            }

            public Address Target { get; }
            public byte[] FnSignature { get; }
            public UInt256 Value { get; }

            public static implicit operator Destination(DestinationTuple tuple) => 
                new Destination(tuple.Item1, tuple.Item2, tuple.Item3);

            public static implicit operator DestinationTuple(Destination destination) => 
                (destination.Target, destination.FnSignature, destination.Value);
        }

        public class DestinationMethodComparer : IComparer<Destination>
        {
            public static readonly DestinationMethodComparer Instance = new DestinationMethodComparer();

            public int Compare(Destination x, Destination y)
            {
                int targetComparison = Comparer<Address>.Default.Compare(x.Target, y.Target);
                if (targetComparison != 0) return targetComparison;
                return Bytes.Comparer.Compare(x.FnSignature, y.FnSignature);
            }
        }
        
        public IDataContract<Address> SendersWhitelist { get; }
        public IDataContract<Destination> MinGasPrices { get; }
        public IDataContract<Destination> Priorities { get; }

        public Transaction SetPriority(Address target, byte[] fnSignature, UInt256 weight) => 
            GenerateTransaction<GeneratedTransaction>(nameof(SetPriority), ContractAddress, target, fnSignature, weight);

        public Transaction SetSendersWhitelist(params Address[] addresses) => 
            GenerateTransaction<GeneratedTransaction>(nameof(SetSendersWhitelist), ContractAddress, (object) addresses);
        
        public Transaction SetMinGasPrice(Address target, byte[] fnSignature, UInt256 weight) => 
            GenerateTransaction<GeneratedTransaction>(nameof(SetMinGasPrice), ContractAddress, target, fnSignature, weight);
    }
}
