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
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;
using DestinationTuple = System.ValueTuple<Nethermind.Core.Address, byte[], Nethermind.Int256.UInt256>;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>
    /// Permission contract for <see cref="ITxPool"/> transaction ordering
    /// <seealso cref="https://github.com/poanetwork/posdao-contracts/blob/master/contracts/TxPriority.sol"/> 
    /// </summary>
    public partial class TxPriorityContract : Contract
    {
        private static readonly object[] MissingSenderWhitelistResult = {Array.Empty<Address>()};
        private static readonly object[] MissingPrioritiesResult = {Array.Empty<DestinationTuple>()};
        private IConstantContract Constant { get; }
        
        public TxPriorityContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) 
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Constant = GetConstant(readOnlyTxProcessorSource);
            SendersWhitelist = new DataContract<Address>(GetSendersWhitelist, SendersWhitelistSet);
            MinGasPrices = new DataContract<Destination>(GetMinGasPrices, MinGasPriceSet);
            Priorities = new DataContract<Destination>(GetPriorities, PrioritySet);
        }

        public Address[] GetSendersWhitelist(BlockHeader parentHeader) =>
            Constant.Call<Address[]>(new CallInfo(parentHeader, nameof(GetSendersWhitelist), ContractAddress) {MissingContractResult = MissingSenderWhitelistResult});

        public Destination[] GetMinGasPrices(BlockHeader parentHeader) =>
            Constant.Call<DestinationTuple[]>(new CallInfo(parentHeader, nameof(GetMinGasPrices), ContractAddress) {MissingContractResult = MissingPrioritiesResult})
                .Select(x => Destination.FromAbiTuple(x, parentHeader.Number)).ToArray();

        public Destination[] GetPriorities(BlockHeader parentHeader) =>
            Constant.Call<DestinationTuple[]>(new CallInfo(parentHeader, nameof(GetPriorities), ContractAddress) {MissingContractResult = MissingPrioritiesResult})
                .Select(x => Destination.FromAbiTuple(x, parentHeader.Number)).ToArray();
        
        public IEnumerable<Destination> PrioritySet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(nameof(PrioritySet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log, blockHeader);
            }
        }
        
        public IEnumerable<Destination> MinGasPriceSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(nameof(MinGasPriceSet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log, blockHeader);
            }
        }
        
        public bool SendersWhitelistSet(BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<Address> items)
        {
            var logEntry = GetSearchLogEntry(nameof(SendersWhitelistSet));

            if (blockHeader.TryFindLog(receipts, logEntry, out LogEntry foundEntry))
            {
                items = DecodeAddresses(foundEntry.Data);
                return true;
            }

            items = Array.Empty<Address>();
            return false;
        }
        
        public Address[] DecodeAddresses(byte[] data)
        {
            var objects = DecodeReturnData(nameof(GetSendersWhitelist), data);
            return (Address[]) objects[0];
        }

        private Destination DecodeDestination(LogEntry log, BlockHeader blockHeader) =>
            new Destination(
                new Address(log.Topics[1]), 
                log.Topics[2].Bytes.Slice(0, 4), 
                AbiType.UInt256.DecodeUInt(log.Data, 0, false).Item1,
                DestinationSource.Contract,
                blockHeader.Number);

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
