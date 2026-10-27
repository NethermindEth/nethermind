// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Facade;
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
        private static readonly Abi<DestinationTuple[]> DestinationsAbi = AbiType.Array(AbiType.Tuple(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256));
        private static readonly AbiSignature<Address[]> AddressesData = new AbiSignature().With(AbiType.Array(AbiType.Address));
        private static readonly AbiSignature<UInt256> DestinationData = new AbiSignature().With(AbiType.UInt256);

        private static readonly ContractFunction<ValueTuple, Address[]> GetSendersWhitelistFunction = new AbiSignature("getSendersWhitelist").With().Returns(AbiType.Array(AbiType.Address));
        private static readonly ContractFunction<ValueTuple, DestinationTuple[]> GetMinGasPricesFunction = new AbiSignature("getMinGasPrices").With().Returns(DestinationsAbi);
        private static readonly ContractFunction<ValueTuple, DestinationTuple[]> GetPrioritiesFunction = new AbiSignature("getPriorities").With().Returns(DestinationsAbi);

        private static readonly AbiSignature<(Address, byte[], UInt256)> PrioritySetEvent = new AbiSignature("PrioritySet").With(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256);
        private static readonly AbiSignature<(Address, byte[], UInt256)> MinGasPriceSetEvent = new AbiSignature("MinGasPriceSet").With(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256);
        private static readonly AbiSignature<Address[]> SendersWhitelistSetEvent = new AbiSignature("SendersWhitelistSet").With(AbiType.Array(AbiType.Address));

        private static readonly AbiSignature<(Address, byte[], UInt256)> SetPrioritySignature = new AbiSignature("setPriority").With(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256);
        private static readonly AbiSignature<Address[]> SetSendersWhitelistSignature = new AbiSignature("setSendersWhitelist").With(AbiType.Array(AbiType.Address));
        private static readonly AbiSignature<(Address, byte[], UInt256)> SetMinGasPriceSignature = new AbiSignature("setMinGasPrice").With(AbiType.Address, AbiType.BytesM(4), AbiType.UInt256);

        private IConstantContract Constant { get; }

        public TxPriorityContract(
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource)
            : base(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Constant = GetConstant(readOnlyTxProcessorSource);
            SendersWhitelist = new DataContract<Address>(GetSendersWhitelist, SendersWhitelistSet);
            MinGasPrices = new DataContract<Destination>(GetMinGasPrices, MinGasPriceSet);
            Priorities = new DataContract<Destination>(GetPriorities, PrioritySet);
        }

        public Address[] GetSendersWhitelist(BlockHeader parentHeader) =>
            Constant.Call(parentHeader, GetSendersWhitelistFunction, Address.SystemUser, default, missingContractResult: []);

        public Destination[] GetMinGasPrices(BlockHeader parentHeader) =>
            Constant.Call(parentHeader, GetMinGasPricesFunction, Address.SystemUser, default, missingContractResult: [])
                .Select(x => Destination.FromAbiTuple(x, parentHeader.Number)).ToArray();

        public Destination[] GetPriorities(BlockHeader parentHeader) =>
            Constant.Call(parentHeader, GetPrioritiesFunction, Address.SystemUser, default, missingContractResult: [])
                .Select(x => Destination.FromAbiTuple(x, parentHeader.Number)).ToArray();

        public IEnumerable<Destination> PrioritySet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            LogEntry logEntry = GetSearchLogEntry(PrioritySetEvent.Hash);

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log, blockHeader);
            }
        }

        public IEnumerable<Destination> MinGasPriceSet(BlockHeader blockHeader, TxReceipt[] receipts)
        {
            LogEntry logEntry = GetSearchLogEntry(MinGasPriceSetEvent.Hash);

            foreach (LogEntry log in blockHeader.FindLogs(receipts, logEntry))
            {
                yield return DecodeDestination(log, blockHeader);
            }
        }

        public bool SendersWhitelistSet(BlockHeader blockHeader, TxReceipt[] receipts, out IEnumerable<Address> items)
        {
            LogEntry logEntry = GetSearchLogEntry(SendersWhitelistSetEvent.Hash);

            if (blockHeader.TryFindLog(receipts, logEntry, out LogEntry foundEntry))
            {
                items = DecodeAddresses(foundEntry.Data);
                return true;
            }

            items = Array.Empty<Address>();
            return false;
        }

        public Address[] DecodeAddresses(byte[] data) => DecodeData(AddressesData, data);

        private Destination DecodeDestination(LogEntry log, BlockHeader blockHeader) =>
            new(
                new Address(log.Topics[1]),
                log.Topics[2].Bytes[..4].ToArray(),
                DecodeData(DestinationData, log.Data),
                DestinationSource.Contract,
                blockHeader.Number);

        public IDataContract<Address> SendersWhitelist { get; }
        public IDataContract<Destination> MinGasPrices { get; }
        public IDataContract<Destination> Priorities { get; }

        public Transaction SetPriority(Address target, byte[] fnSignature, UInt256 weight) =>
            GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(SetPrioritySignature, (target, fnSignature, weight)), ContractAddress);

        public Transaction SetSendersWhitelist(params Address[] addresses) =>
            GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(SetSendersWhitelistSignature, addresses), ContractAddress);

        public Transaction SetMinGasPrice(Address target, byte[] fnSignature, UInt256 weight) =>
            GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(SetMinGasPriceSignature, (target, fnSignature, weight)), ContractAddress);
    }
}
