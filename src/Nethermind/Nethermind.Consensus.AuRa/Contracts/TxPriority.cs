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
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>
    /// Permission contract for <see cref="ITxPool"/> transaction ordering
    /// <seealso cref="https://github.com/poanetwork/posdao-contracts/blob/tx-priority/contracts/TxPriority.sol"/> 
    /// </summary>
    public class TxPermission : Contract
    {
        private ConstantContract Constant { get; }
        
        public TxPermission(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) 
            : base(abiEncoder, contractAddress)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
            SendersWhitelist = new DataContract<Address>(GetSendersWhitelist, SendersWhitelistSet, false);
            MinGasPrice = new DataContract<Destination>(GetMinGasPrices, MinGasPriceSet, true);
            Priority = new DataContract<Destination>(GetPriorities, PrioritySet, true);
        }

        private Address[] GetSendersWhitelist(BlockHeader parentHeader) => Constant.Call<Address[]>(parentHeader, nameof(GetSendersWhitelist), Address.Zero);
        
        private Destination[] GetMinGasPrices(BlockHeader parentHeader) => Constant.Call<Destination[]>(parentHeader, nameof(GetMinGasPrices), Address.Zero);
        
        private Destination[] GetPriorities(BlockHeader parentHeader) => Constant.Call<Destination[]>(parentHeader, nameof(GetPriorities), Address.Zero);
        
        private IEnumerable<Destination> PrioritySet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(PrioritySet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log.Data);
            }
        }
        
        private IEnumerable<Destination> MinGasPriceSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(MinGasPriceSet));

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log.Data);
            }
        }
        
        private IEnumerable<Address> SendersWhitelistSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            var logEntry = GetSearchLogEntry(blockHeader, nameof(MinGasPriceSet));

            return blockHeader.TryFindLog(receipts, logEntry, LogEntryAddressAndTopicEqualityComparer.Instance, out LogEntry foundEntry) 
                ? DecodeAddresses(foundEntry.Data) 
                : Array.Empty<Address>();
        }
        
        private Address[] DecodeAddresses(byte[] data)
        {
            var objects = DecodeReturnData(nameof(GetSendersWhitelist), data);
            return (Address[]) objects[0];;
        }

        private Destination DecodeDestination(byte[] data)
        {
            throw new NotImplementedException();
        }

        public struct Destination
        {
            private Address Target { get; set; }
            private byte[] FnSignature { get; set; }
            private UInt256 Value { get; set; }
        }

        public IDataContract<Address> SendersWhitelist { get; }
        public IDataContract<Destination> MinGasPrice { get; }
        public IDataContract<Destination> Priority { get; }
    }
}
