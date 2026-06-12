// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Init.Steps;

/// <summary>
/// Measurement #2 for speculative pre-execution: of the suggested block's transactions that
/// are in the local pool, how many would survive read-set validation at their in-block
/// position had they been executed against the parent state. Two shadow passes per block on
/// read-only scopes, entirely off the processing path. Strictly conservative: same-sender
/// nonce chains fail pass 1 (counted as spec fails) and any value drift counts as a conflict.
/// Replay skips pre-block system calls (e.g. beacon root), so a small exec-fail tail is expected.
/// </summary>
public sealed class SpeculativeSurvivalDiag(ILifetimeScope context, ITxPool txPool, IBlockTree blockTree, IEthereumEcdsa ecdsa, ILogManager logManager)
{
    private readonly Lazy<IReadOnlyTxProcessingEnvFactory> _envFactory = new(context.Resolve<IReadOnlyTxProcessingEnvFactory>);
    private readonly ILogger _logger = logManager.GetClassLogger<SpeculativeSurvivalDiag>();

    private int _inFlight;
    private long _totalGas;
    private long _speculatedGas;
    private long _survivedGas;

    public void OnNewSuggestedBlock(Block? block)
    {
        if (block is null || block.Transactions.Length == 0) return;
        // Catch-up bursts suggest many blocks at once; measure one at a time and drop the rest.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return;
        Task.Run(() =>
        {
            try
            {
                Measure(block);
            }
            catch (Exception e)
            {
                if (_logger.IsInfo) _logger.Info($"SpecSurvDiag: block {block.Number} measurement failed: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _inFlight, 0);
            }
        });
    }

    private void Measure(Block block)
    {
        BlockHeader? parent = blockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
        if (parent is null) return;

        Transaction[] transactions = block.Transactions;
        Transaction?[] pooled = new Transaction?[transactions.Length];
        int pooledCount = 0;
        for (int i = 0; i < transactions.Length; i++)
        {
            if (transactions[i].Hash is not null && txPool.TryGetPendingTransaction(transactions[i].Hash!, out Transaction? poolTransaction))
            {
                pooled[i] = poolTransaction;
                pooledCount++;
            }
        }

        if (pooledCount == 0) return;

        using IReadOnlyTxProcessorSource source = _envFactory.Value.Create();

        // The transaction processor accumulates GasUsed into the header it executes under.
        // block.Header is the live object shared with the block tree and real processing —
        // shadow passes MUST run on private clones or they corrupt consensus (base fee of
        // the next block is computed from parent GasUsed).
        // The suggested header carries the block's FINAL GasUsed; execution starts from zero
        // or every transaction trips the block gas limit check.
        BlockHeader speculationHeader = block.Header.Clone();
        speculationHeader.GasUsed = 0;
        BlockHeader replayHeader = block.Header.Clone();
        replayHeader.GasUsed = 0;

        int specFailed = 0;
        Dictionary<int, ReadSet> readSets = new(pooledCount);
        using (IReadOnlyTxProcessingScope scope = source.Build(parent))
        {
            for (int i = 0; i < transactions.Length; i++)
            {
                Transaction? speculated = pooled[i];
                if (speculated is null) continue;
                AccessTxTracer tracer = new();
                TransactionResult result = scope.TransactionProcessor.CallAndRestore(speculated, speculationHeader, tracer);
                if (result && tracer.AccessList is not null)
                {
                    readSets[i] = ReadSet.Capture(scope.WorldState, tracer.AccessList, block.Header.GasBeneficiary);
                }
                else
                {
                    specFailed++;
                }
            }
        }

        int survived = 0;
        int execFailed = 0;
        long blockGas = 0;
        long specGas = 0;
        long survGas = 0;
        using (IReadOnlyTxProcessingScope scope = source.Build(parent))
        {
            for (int i = 0; i < transactions.Length; i++)
            {
                Transaction transaction = transactions[i];
                blockGas += transaction.GasLimit;
                if (readSets.TryGetValue(i, out ReadSet? readSet))
                {
                    specGas += transaction.GasLimit;
                    if (readSet!.MatchesCurrent(scope.WorldState))
                    {
                        survived++;
                        survGas += transaction.GasLimit;
                    }
                }

                // Same canonical value RecoverSignatures writes on the processing thread;
                // an atomic reference write of an equal value is safe to race.
                transaction.SenderAddress ??= pooled[i]?.SenderAddress ?? ecdsa.RecoverAddress(transaction);
                if (transaction.SenderAddress is null || !scope.TransactionProcessor.Execute(transaction, replayHeader, NullTxTracer.Instance))
                {
                    execFailed++;
                }
            }
        }

        long totalGas = Interlocked.Add(ref _totalGas, blockGas);
        long cumulativeSpecGas = Interlocked.Add(ref _speculatedGas, specGas);
        long cumulativeSurvGas = Interlocked.Add(ref _survivedGas, survGas);
        if (_logger.IsInfo)
            _logger.Info(
                $"SpecSurvDiag: block {block.Number} txs {transactions.Length}, spec {readSets.Count}, survived {survived} ({Pct(survived, readSets.Count):F1}% of spec), " +
                $"gas surv {Pct(survGas, blockGas):F1}% of block; cumulative gas: spec {Pct(cumulativeSpecGas, totalGas):F1}%, surv {Pct(cumulativeSurvGas, totalGas):F1}%, " +
                $"surv/spec {Pct(cumulativeSurvGas, cumulativeSpecGas):F1}%; fails spec {specFailed} exec {execFailed}");
    }

    private static double Pct(long part, long whole) => whole == 0 ? 0 : 100.0 * part / whole;
    private static double Pct(int part, int whole) => whole == 0 ? 0 : 100.0 * part / whole;

    /// <summary>
    /// Values a speculative execution read (approximated by its access list — a superset of
    /// the true read-set, so strictly conservative). Coinbase is excluded: every transaction
    /// credits it, and a production design applies fees as commutative deltas.
    /// </summary>
    private sealed class ReadSet
    {
        private readonly List<(Address Address, UInt256 Balance, UInt256 Nonce, ValueHash256 CodeHash)> _accounts = [];
        private readonly List<(StorageCell Cell, byte[] Value)> _slots = [];

        public static ReadSet Capture(IWorldState state, AccessList accessList, Address? coinbase)
        {
            ReadSet readSet = new();
            foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList)
            {
                if (address == coinbase) continue;
                readSet._accounts.Add((address, state.GetBalance(address), state.GetNonce(address), state.GetCodeHash(address)));
                foreach (UInt256 storageKey in storageKeys)
                {
                    StorageCell cell = new(address, storageKey);
                    readSet._slots.Add((cell, state.Get(cell).ToArray()));
                }
            }

            return readSet;
        }

        public bool MatchesCurrent(IWorldState state)
        {
            foreach ((Address address, UInt256 balance, UInt256 nonce, ValueHash256 codeHash) in _accounts)
            {
                if (state.GetBalance(address) != balance || state.GetNonce(address) != nonce || state.GetCodeHash(address) != codeHash)
                {
                    return false;
                }
            }

            foreach ((StorageCell cell, byte[] value) in _slots)
            {
                if (!Bytes.AreEqual(state.Get(cell), value)) return false;
            }

            return true;
        }
    }
}
