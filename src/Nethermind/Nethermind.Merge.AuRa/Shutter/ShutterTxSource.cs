// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Abi;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Facade.Filters;
using Nethermind.Int256;
using Nethermind.Core.Crypto;
using System.Security.Policy;
using Multiformats.Hash.Algorithms;

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

    // todo: create data class for log data ABI?
    private IEnumerable<object[]> GetEvents()
    {
        IEnumerable<FilterLog> logs = _logFinder!.FindLogs(_logFilter!);
        return logs.Select(log => AbiEncoder.Instance.Decode(AbiEncodingStyle.None, ABI_SIGNATURE, log.Data));
    }

    class SequencedTransaction
    {
        public UInt64 Eon;
        public byte[]? EncryptedTransaction;
        public UInt256 GasLimit;
        public G1 Identity; // todo: create G1 field element type

        public SequencedTransaction(UInt64 eon, byte[] encryptedTransaction, UInt256 gasLimit, G1 identity)
        {
            Eon = eon;
            EncryptedTransaction = encryptedTransaction;
            GasLimit = gasLimit;
            Identity = identity;
        }
    }

    // cyclic group defined on the curve alt_bn128
    class G1
    {
        private readonly UInt256 _x;
        private readonly UInt256 _y;

        private G1(UInt256 x, UInt256 y)
        {
            _x = x;
            _y = y;
        }

        public G1 ScalarBaseMult(UInt256 i)
        {
            return new G1(_x * i, _y * i);
        }

        public static G1 Generator()
        {
            return new G1(1, 2);
        }
    }

    private G1 ComputeIdentity(Bytes32 identityPrefix, Address sender)
    {
        byte[] tmp = new byte[52];
        identityPrefix.Unwrap().CopyTo(tmp, 0);
        sender.Bytes.CopyTo(tmp, 32);

        Keccak hash = new(tmp);
        return G1.Generator().ScalarBaseMult(new UInt256(hash.Bytes));
    }

    private IEnumerable<SequencedTransaction> GetNextTransactions(UInt64 eon, int txPointer)
    {
        IEnumerable<object[]> events = GetEvents();
        events = events.Where(e => (UInt64) e[1] == eon).Skip(txPointer);

        IEnumerable<SequencedTransaction> txs = new List<SequencedTransaction>();
        UInt256 totalGas = 0;

        foreach(object[] e in events)
        {
            if (totalGas + (UInt256)e[5] > ENCRYPTED_GAS_LIMIT)
            {
                break;
            }

            txs.Append(new SequencedTransaction(
                eon,
                (byte[])e[4],
                (UInt256)e[5],
                ComputeIdentity((Bytes32) e[2], (Address)e[3])
            ));

            totalGas += (UInt256)e[5];
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
        IEnumerable<SequencedTransaction> encryptedTransactions = GetNextTransactions(0, 0);
        // todo: decrypt transactions
        return Enumerable.Empty<Transaction>();
    }
}
