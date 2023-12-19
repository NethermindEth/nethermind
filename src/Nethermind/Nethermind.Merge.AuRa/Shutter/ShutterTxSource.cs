// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Consensus.Producers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Merge.AuRa.Test")]

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource : ITxSource
{

    private ILogFinder? _logFinder;
    private LogFilter? _logFilter;
    private static readonly Address SequencerAddress = Address.Zero;
    private static readonly UInt256 EncryptedGasLimit = 300;
    internal static readonly AbiSignature TransactionSubmmitedSig = new AbiSignature(
        "TransactionSubmitted",
        [
            AbiType.UInt64, // eon
            AbiType.Bytes32, // identity prefix
            AbiType.Address, // sender
            AbiType.DynamicBytes, // encrypted transaction
            AbiType.UInt256 // gas limit
        ]
    );

    public ShutterTxSource(ILogFinder logFinder, IFilterStore filterStore)
        : base()
    {
        IEnumerable<object> topics = new List<object>() {TransactionSubmmitedSig.Hash};
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, SequencerAddress.ToString(), topics);
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
    {
        // todo: get eon and txpointer
        ulong eon = 0;
        int txPointer = 0;

        // get encrypted transactions from event logs
        IEnumerable<SequencedTransaction> encryptedTransactions = GetNextTransactions(eon, txPointer);
        
        // todo: get decryption key from gossip layer and decrypt transactions

        return Enumerable.Empty<Transaction>();
    }

    
    private IEnumerable<TransactionSubmittedEvent> GetEvents()
    {
        IEnumerable<IFilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => new TransactionSubmittedEvent(AbiEncoder.Instance.Decode(AbiEncodingStyle.None, TransactionSubmmitedSig, log.Data)));
    }

    private object? ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        return null;
    }

    internal IEnumerable<SequencedTransaction> GetNextTransactions(UInt64 eon, int txPointer)
    {
        IEnumerable<TransactionSubmittedEvent> events = GetEvents();
        events = events.Where(e => e.Eon == eon).Skip(txPointer);

        List<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach(TransactionSubmittedEvent e in events)
        {
            if (totalGas + e.GasLimit > EncryptedGasLimit)
            {
                break;
            }

            txs.Add(new SequencedTransaction(
                eon,
                e.EncryptedTransaction,
                e.GasLimit,
                ComputeIdentity(e.IdentityPrefix, e.Sender)
            ));

            totalGas += e.GasLimit;
        }

        return txs;
    }

    internal class SequencedTransaction
    {
        public ulong Eon;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;
        public object? Identity;

        public SequencedTransaction(UInt64 eon, byte[] encryptedTransaction, UInt256 gasLimit, object? identity)
        {
            Eon = eon;
            EncryptedTransaction = encryptedTransaction;
            GasLimit = gasLimit;
            Identity = identity;
        }
    }

    internal class TransactionSubmittedEvent
    {
        public ulong Eon;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;

        public TransactionSubmittedEvent(object[] decodedEvent)
        {
            Eon = (ulong)decodedEvent[0];
            IdentityPrefix = new Bytes32((byte[])decodedEvent[1]);
            Sender = (Address)decodedEvent[2];
            EncryptedTransaction = (byte[])decodedEvent[3];
            GasLimit = (UInt256)decodedEvent[4];
        }
    }

}
