// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Eez.Execution.Stateless;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class RollupXObservationReplay
{
    private readonly ISpecProvider _specs;
    private readonly EezChainRules _rules;
    private readonly ILogManager _logs;
    private readonly EezStatelessExecutor _executor;

    internal RollupXObservationReplay(ISpecProvider specs, EezChainRules rules, ILogManager logs)
        : this(specs, rules, logs, new EezStatelessExecutor(specs, rules, logs)) { }

    internal RollupXObservationReplay(NativeGenesisSnapshot profile, NativeL1HeaderChain evidence, ILogManager logs)
        : this(profile.Specs, profile.Rules, logs, new EezStatelessExecutor(profile, evidence, logs)) { }

    private RollupXObservationReplay(ISpecProvider specs, EezChainRules rules, ILogManager logs, EezStatelessExecutor executor)
    {
        _specs = specs;
        _rules = rules;
        _logs = logs;
        _executor = executor;
    }

    public void Verify(EezStatelessBlock input, EezStatelessBlockResult validated,
        IReadOnlyList<RollupXObservationClaim> claims, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_rules.IsRollupX) throw new EezSettlementException("Observation replay requires RollupX chain rules.");
        Block block = Rlp.Decode<Block>(input.Rlp) ?? throw new EezSettlementException("Observation block RLP is empty.");
        if (block.Hash != validated.Hash) throw new EezSettlementException("Observation block differs from the validated terminal block.");
        RollupXCheckpointPlan plan = RollupXCheckpointPlan.Create(block);
        Hash256[] pins = new Hash256[claims.Count];
        for (int i = 0; i < pins.Length; i++) pins[i] = claims[i].Pin;
        int[] positions = plan.ObservationPositions(pins, validated.Checkpoints, validated.Hash, block.Transactions.Length);
        NativeCheckpointProjection? projection = _rules.FinalizesProtocolBalances ? new(_rules.ProtocolBalanceAccounts, _logs) : null;
        Scheduler scheduler = new(_specs, _rules, _logs, claims, positions, validated.Checkpoints, projection, cancellationToken);
        _executor.AssignSenders(block);
        StatelessBlockProcessingEnv environment = _executor.CreateEnvironment(input.Witness, scheduler, scheduler, scheduler.Wrap, projection);
        StatelessBlockProcessingResult result = environment.Process(block);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsValid || result.ProcessedBlock!.Hash != validated.Hash
            || result.ProcessedBlock.Header.StateRoot != validated.PostStateRoot || result.Parent!.StateRoot != validated.PreStateRoot)
            throw new EezSettlementException("Observation replay changed the validated block result.");
        scheduler.EnsureConsumed();
    }

    private sealed class Scheduler(ISpecProvider specs, EezChainRules rules, ILogManager logs,
        IReadOnlyList<RollupXObservationClaim> claims, int[] positions, EezTransactionCheckpoint[] checkpoints,
        NativeCheckpointProjection? projection,
        CancellationToken cancellationToken)
        : ITransactionProcessorFactory, BlockProcessor.BlockValidationTransactionsExecutor.ITransactionProcessedEventHandler
    {
        private readonly EezTransactionProcessorFactory _canonical = new(rules);
        private IWorldState _state = null!;
        private RollupXObservationCall _call = null!;
        private int _next;

        public ITransactionProcessor Create(ITransactionProcessor.IBlobBaseFeeCalculator blobBaseFeeCalculator,
            ISpecProvider specProvider, IWorldState worldState, IVirtualMachine virtualMachine,
            ICodeInfoRepository codeInfoRepository, ILogManager logManager, bool parallel)
        {
            if (parallel || _call is not null) throw new EezSettlementException("Observation replay requires one sequential processor.");
            _state = worldState;
            _call = new(specs, worldState, virtualMachine, codeInfoRepository, logs);
            return _canonical.Create(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);
        }

        public IBlockProcessor.IBlockTransactionsExecutor Wrap(IBlockProcessor.IBlockTransactionsExecutor inner, IWorldState worldState) =>
            new BoundaryExecutor(inner, this);

        public void OnTransactionProcessed(TxProcessedEventArgs args) => AtBoundary(args.Index + 1, args.BlockHeader);

        public void EnsureConsumed()
        {
            if (_next != claims.Count) throw new EezSettlementException("Observation replay did not consume every FIFO claim.");
        }

        private void AtBoundary(int position, BlockHeader header)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_next == positions.Length || positions[_next] != position) return;
            _state.Commit(specs.GetSpec(header), commitRoots: true);
            _state.RecalculateStateRoot();
            Hash256 rawRoot = _state.StateRoot;
            Hash256 expectedRoot = PrefixRoot(position);
            Hash256 checkpointRoot = projection?.Project(_state, specs.GetSpec(header)) ?? rawRoot;
            if (checkpointRoot != expectedRoot) throw new EezSettlementException("Observation state differs from its validated transaction prefix.");
            while (_next < positions.Length && positions[_next] == position)
            {
                RollupXObservationClaim claim = claims[_next];
                _call.VerifyClaim(claim, rules.Eezl2Address, cancellationToken);
                _next++;
            }
            _state.RecalculateStateRoot();
            if (_state.StateRoot != rawRoot) throw new EezSettlementException("Observation replay changed its transaction prefix state.");
        }

        private Hash256 PrefixRoot(int position)
        {
            foreach (EezTransactionCheckpoint checkpoint in checkpoints)
                if (checkpoint.TransactionIndex + 1 == position) return checkpoint.StateRoot;
            throw new EezSettlementException("Observation prefix has no independently validated state root.");
        }

        private sealed class BoundaryExecutor(IBlockProcessor.IBlockTransactionsExecutor inner, Scheduler scheduler)
            : IBlockProcessor.IBlockTransactionsExecutor
        {
            public TxReceipt[] ProcessTransactions(Block block, ProcessingOptions processingOptions, BlockReceiptsTracer receiptsTracer, CancellationToken token = default)
            {
                scheduler.AtBoundary(0, block.Header);
                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);
            }

            public void SetBlockExecutionContext(in BlockExecutionContext context) => inner.SetBlockExecutionContext(in context);
            public void PublishTransactionProcessedEvents() => inner.PublishTransactionProcessedEvents();
            public void ClearTransactionProcessedEvents() => inner.ClearTransactionProcessedEvents();
            public void SetupTxTimingMetrics(Block block) => inner.SetupTxTimingMetrics(block);
            public long StartTxTimer() => inner.StartTxTimer();
            public void StopTxTimer(int i, long txStart) => inner.StopTxTimer(i, txStart);
        }
    }
}

internal sealed record RollupXObservationClaim(Hash256 Pin, Address SourceAddress, ulong SourceRollupId,
    Address Target, byte[] Data, UInt256 Value, ObservationMode Mode, ObservationOutcome Expected)
{
    internal RollupXObservationCallback[] Callbacks { get; init; } = [];
}

internal sealed record RollupXObservationCallback(Address Source, Address Target, byte[] Data, byte[] Output);
