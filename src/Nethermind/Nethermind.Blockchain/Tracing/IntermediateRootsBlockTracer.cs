// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Blockchain.Tracing;

/// <summary>
/// Captures the world state root after each transaction in a block.
/// Mirrors geth's <c>debug_intermediateRoots</c> behaviour.
/// </summary>
/// <remarks>
/// Roots are produced per transaction in execution order; a failed transaction still
/// produces its post-execution root (matching geth's partial-result semantics).
/// System-level calls (EIP-4788, EIP-2935) and withdrawals do not produce entries
/// because <see cref="BlockTracerBase{TTrace,TTracer}"/> only dispatches on user transactions.
/// </remarks>
public class IntermediateRootsBlockTracer(IWorldState worldState, IReleaseSpec spec)
    : BlockTracerBase<Hash256, IntermediateRootsBlockTracer.IntermediateRootsTxTracer>
{
    protected override IntermediateRootsTxTracer OnStart(Transaction? tx) => new(worldState, spec);

    protected override Hash256 OnEnd(IntermediateRootsTxTracer txTracer) => txTracer.StateRoot;

    public class IntermediateRootsTxTracer(IWorldState worldState, IReleaseSpec spec) : TxTracer
    {
        public override bool IsTracingReceipt => true;

        public Hash256 StateRoot { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            Capture(stateRoot);

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
            Capture(stateRoot);

        private void Capture(Hash256? reportedStateRoot)
        {
            if (reportedStateRoot is not null)
            {
                StateRoot = reportedStateRoot;
                return;
            }

            // Post-Byzantium (EIP-658): TransactionProcessor commits with commitRoots=false,
            // which buffers changes into _blockChanges without flushing them to the trie.
            // Force a flush so the recomputed root reflects this transaction's effect — the
            // BlockProcessor's later end-of-block commit is idempotent for already-flushed entries.
            worldState.Commit(spec, commitRoots: true);
            worldState.RecalculateStateRoot();
            StateRoot = worldState.StateRoot;
        }
    }
}
