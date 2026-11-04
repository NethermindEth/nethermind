// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;
using Nethermind.TxPool.Comparison;
using Nethermind.TxPool.Filters;

namespace Nethermind.Benchmarks.TxPool;

[MemoryDiagnoser]
public class KeyedNonceOverlapBenchmarks
{
    private IIncomingTxFilter _filter = null!;
    private Transaction _incoming = null!;

    [Params(1, 3, 16)]
    public int KeyCount { get; set; }

    [ParamsAllValues]
    public OverlapPosition Position { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        UInt256[] incoming = new UInt256[KeyCount];
        UInt256[] pending = new UInt256[KeyCount];
        for (int i = 0; i < KeyCount; i++)
        {
            incoming[i] = (UInt256)(2 * i);
            pending[i] = (UInt256)(2 * i + 1);
        }
        int overlapIndex = Position switch
        {
            OverlapPosition.Disjoint => -1,
            OverlapPosition.First => 0,
            OverlapPosition.Middle => KeyCount / 2,
            OverlapPosition.Last => KeyCount - 1,
            _ => throw new ArgumentOutOfRangeException()
        };
        if (overlapIndex >= 0) pending[overlapIndex] = incoming[overlapIndex];
        if (!KeyedNonceManager.AreNonceKeysWellFormed(incoming) || !KeyedNonceManager.AreNonceKeysWellFormed(pending))
            throw new InvalidOperationException("The benchmark requires valid nonce key sets.");

        _incoming = FrameTransaction(incoming, nonce: 1);
        Transaction pendingTx = FrameTransaction(pending, nonce: 0);
        TxDistinctSortedPool standardPool = new(1, CompareTxByNonce.Instance, LimboLogs.Instance);
        TxDistinctSortedPool blobPool = new(1, CompareTxByNonce.Instance, LimboLogs.Instance);
        if (!standardPool.TryInsert(pendingTx.Hash!, pendingTx))
            throw new InvalidOperationException("The benchmark pool is incomplete.");

        Type filterType = typeof(IIncomingTxFilter).Assembly.GetType("Nethermind.TxPool.Filters.KeyedNonceDisjointnessFilter", throwOnError: true)!;
        _filter = (IIncomingTxFilter)Activator.CreateInstance(filterType, standardPool, blobPool)!;
        AcceptTxResult expected = overlapIndex >= 0 ? AcceptTxResult.KeyedNonceOverlap : AcceptTxResult.Accepted;
        if (Accept() != expected)
            throw new InvalidOperationException("The benchmark filter result does not match its case.");
    }

    [Benchmark]
    public AcceptTxResult Accept()
    {
        TxFilteringState state = default;
        return _filter.Accept(_incoming, ref state, TxHandlingOptions.None);
    }

    private static Transaction FrameTransaction(UInt256[] keys, ulong nonce) =>
        new()
        {
            Type = TxType.FrameTx,
            Hash = nonce == 0 ? TestItem.KeccakA : TestItem.KeccakB,
            SenderAddress = TestItem.AddressA,
            Nonce = nonce,
            NonceKeys = keys,
            Frames = [],
            FrameSignatures = [],
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 1,
        };

    public enum OverlapPosition
    {
        Disjoint,
        First,
        Middle,
        Last
    }
}
