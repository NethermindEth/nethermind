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
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.AuRa.Shutter;

public class ShutterTxSource : ITxSource
{

    private ILogFinder? _logFinder;
    private LogFilter? _logFilter;
    private static readonly Address SEQUENCER_ADDRESS = new(new Keccak("0x0"));
    private static readonly UInt256 ENCRYPTED_GAS_LIMIT = 1000;
    private static readonly AbiSignature ABI_SIGNATURE = new AbiSignature(
        "TransactionSubmitted",
        new AbiType[] {
            AbiType.UInt64,
            AbiType.Bytes32,
            AbiType.Address,
            AbiType.DynamicBytes,
            AbiType.UInt256
        }
    );
    
    class SequencedTransaction
    {
        public UInt64 Eon;
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

    class TransactionSubmittedEvent
    {
        public UInt64 Eon;
        public Bytes32 IdentityPrefix;
        public Address Sender;
        public byte[] EncryptedTransaction;
        public UInt256 GasLimit;

        public TransactionSubmittedEvent(object[] decodedEvent)
        {
            Eon = (UInt64)decodedEvent[1];
            IdentityPrefix = (Bytes32)decodedEvent[2];
            Sender = (Address)decodedEvent[3];
            EncryptedTransaction = (byte[])decodedEvent[4];
            GasLimit = (UInt256)decodedEvent[5];
        }
    }

    private IEnumerable<TransactionSubmittedEvent> GetEvents()
    {
        IEnumerable<FilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => new TransactionSubmittedEvent(AbiEncoder.Instance.Decode(AbiEncodingStyle.None, ABI_SIGNATURE, log.Data)));
    }

    private object? ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        return null;
    }

    private IEnumerable<SequencedTransaction> GetNextTransactions(UInt64 eon, int txPointer)
    {
        IEnumerable<TransactionSubmittedEvent> events = GetEvents();
        events = events.Where(e => e.Eon == eon).Skip(txPointer);

        IEnumerable<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach(TransactionSubmittedEvent e in events)
        {
            if (totalGas + e.GasLimit > ENCRYPTED_GAS_LIMIT)
            {
                break;
            }

            txs.Append(new SequencedTransaction(
                eon,
                e.EncryptedTransaction,
                e.GasLimit,
                ComputeIdentity(e.IdentityPrefix, e.Sender)
            ));

            totalGas += e.GasLimit;
        }

        return txs;
    }

    public ShutterTxSource(ILogFinder logFinder, IFilterStore filterStore)
        : base()
    {
        IEnumerable<object> topics = new List<object>() {ABI_SIGNATURE.Hash};
        _logFinder = logFinder;
        _logFilter = filterStore.CreateLogFilter(BlockParameter.Earliest, BlockParameter.Latest, SEQUENCER_ADDRESS, topics);
    }

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
    {
        // todo: get eon and txpointer
        ulong eon = 0;
        int txPointer = 0;

        // get encrypted transactions from event logs
        IEnumerable<SequencedTransaction> encryptedTransactions = GetNextTransactions(eon, txPointer);
        
        // todo: get decryption key from gossip layer and decrypt transactions

        return Enumerable.Empty<Transaction>();
    }
}
