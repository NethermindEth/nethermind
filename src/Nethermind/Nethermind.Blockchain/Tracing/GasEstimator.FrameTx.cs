// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public partial class GasEstimator
{
    private const int MaxFrameProbes = 512;
    private const double OptimisticMultiplier = 64d / 63d;
    private const string InvalidErrorMarginNegative = "Invalid error margin, cannot be negative.";
    private const int RebalanceLevels = 2;

    /// <summary>Fills omitted frame limits by replaying the complete transaction in the caller's state scope.</summary>
    /// <remarks>Every probe keeps its omitted execution and state limits within the rooms the final limits must fit,
    /// and its frame limits plus signature verification work within <paramref name="gasCap"/>, the work bound
    /// <c>FrameTransactionForRpc.ToTransaction</c> enforces on explicit limits. Frames are minimised in order: the
    /// frame being searched takes what the others leave, while each later frame holds a reservation measured by a
    /// first probe that splits the rooms evenly. Probes run at the requested fees, or unpriced when the payer cannot
    /// afford the rooms at them, and a final probe at the requested fees checks the filled transaction.</remarks>
    /// <param name="context">The block the estimate runs in; each probe runs in a copy of its header.</param>
    /// <param name="executionReverted">Whether the failure is a frame of an otherwise valid transaction reverting.</param>
    public Result<TxFrame[]> EstimateFrameGas(Transaction transaction, BlockExecutionContext context,
        bool[] fillExecution, bool[] fillState, ulong gasCap, int errorMargin, CancellationToken token, out bool executionReverted)
    {
        executionReverted = false;
        if (errorMargin < 0 || errorMargin >= MaxErrorMargin)
            return Result<TxFrame[]>.Fail(errorMargin < 0 ? InvalidErrorMarginNegative : InvalidErrorMarginTooHigh);
        if (transaction.Frames is not { Length: > 0 and <= Eip8141Constants.MaxFrames })
            return Result<TxFrame[]>.Fail(FrameTxValidation.MissingFrames);
        Transaction tx = new();
        transaction.CopyTo(tx, copyHash: false);
        TxFrame[] frames = (TxFrame[])transaction.Frames.Clone();
        tx.Frames = frames;
        tx.SenderAddress ??= Address.Zero;
        tx.Nonce = stateProvider.GetNonce(tx.SenderAddress);

        Span<FrameReservation> reservations = stackalloc FrameReservation[Eip8141Constants.MaxFrames];
        FrameGasSearch search = new(transactionProcessor, tx, context, fillExecution, fillState, gasCap, errorMargin, token,
            reservations[..frames.Length]);
        if (!search.TryPartitionRooms()) return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);
        search.ReserveEvenSplit();
        search.MeasureReservations();
        if (!search.TryMinimizeFrames(out string? error))
        {
            executionReverted = search.LastProbeReverted;
            return Result<TxFrame[]>.Fail(error);
        }
        if (!search.FitsRooms()) return Result<TxFrame[]>.Fail(CannotEstimateGasExceeded);
        return search.Verify(out executionReverted);
    }

    private static ulong Used(TxFrameReceipt receipt, bool execution) => execution ? receipt.ExecutionGasUsed : receipt.StateGasUsed;

    private static ulong Optimistic(ulong used, ulong high, bool execution) =>
        Math.Min(high, execution ? (ulong)Math.Ceiling(used * OptimisticMultiplier) : used);

    private static ulong Deduct(ulong left, ulong amount) => left > amount ? left - amount : 0;

    private static TxFrame WithGas(TxFrame frame, ulong execution, ulong state) =>
        frame.ExecutionGasLimit == execution && frame.StateGasLimit == state
            ? frame
            : new(frame.Mode, frame.Flags, frame.Target, execution, state, frame.Value, frame.Data);

    private struct FrameReservation
    {
        public ulong Execution;
        public ulong State;
        public bool OutgrewSplit;
    }

    /// <summary>The state of one <see cref="EstimateFrameGas"/> call, whose phases run in declaration order.</summary>
    /// <remarks>Probes share one header copy and one tracer, both reset before each run; each probe still gets a fresh
    /// transaction copy, since execution memoizes its gas budget on the transaction.</remarks>
    private ref struct FrameGasSearch
    {
        private readonly ITransactionProcessor _processor;
        private readonly Transaction _tx;
        private readonly TxFrame[] _frames;
        private readonly bool[] _fillExecution;
        private readonly bool[] _fillState;
        private readonly Span<FrameReservation> _reservations;
        private readonly IReleaseSpec _spec;
        private readonly UInt256 _baseFee;
        private readonly BlockHeader _probeHeader;
        private readonly BlockExecutionContext _probeContext;
        private readonly FrameEstimateTracer _tracer = new();
        private readonly CancellationTxTracer _probeTracer;
        private readonly CancellationToken _token;
        private readonly ulong _gasCap;
        private readonly ulong _executionCap;
        private readonly ulong _stateCap;
        private readonly int _errorMargin;
        private readonly int _executionCount;
        private readonly int _stateCount;
        private ulong _executionRoom;
        private ulong _stateRoom;
        private ulong _executionPool;
        private ulong _statePool;
        private bool _capLimited;
        private int _probes;
        private bool _probesExhausted;
        private bool _unpricedSearch;
        private TransactionResult _lastResult;
        private TxFrameReceipt[]? _receipts;
        private (int? Frame, EvmExceptionType? Error) _reservationFailure;

        public FrameGasSearch(ITransactionProcessor processor, Transaction tx, in BlockExecutionContext context, bool[] fillExecution,
            bool[] fillState, ulong gasCap, int errorMargin, CancellationToken token, Span<FrameReservation> reservations)
        {
            _processor = processor;
            _tx = tx;
            _frames = tx.Frames!;
            _fillExecution = fillExecution;
            _fillState = fillState;
            _reservations = reservations;
            _spec = context.Spec;
            _baseFee = context.Header.BaseFeePerGas;
            _probeHeader = context.Header.Clone();
            _probeContext = new BlockExecutionContext(_probeHeader, _spec, new UInt256(context.BlobBaseFee.Bytes, isBigEndian: true));
            _probeTracer = _tracer.WithCancellation(token);
            _token = token;
            _gasCap = gasCap;
            _executionCap = Math.Min(gasCap, Math.Min(context.Header.GasLimit, Eip7825Constants.DefaultTxGasLimitCap));
            _stateCap = Math.Min(gasCap, context.Header.GasLimit);
            _errorMargin = errorMargin;
            for (int i = 0; i < _frames.Length; i++)
            {
                if (fillExecution[i]) _executionCount++;
                if (fillState[i]) _stateCount++;
            }
        }

        public bool LastProbeReverted { get; private set; }

        /// <summary>Sizes the pools the omitted limits share, or fails when the fixed gas leaves no room for them.</summary>
        public bool TryPartitionRooms()
        {
            if (!TryReserveBlockRooms(out ulong reservedExecution, out ulong reservedState)) return false;
            ulong fixedGas = FixedGas();
            if (fixedGas > _gasCap) return false;

            ulong fillBudget = _gasCap - fixedGas;
            _executionRoom = _executionCap - reservedExecution;
            _stateRoom = _stateCap - reservedState;
            _executionPool = _executionCount == 0 ? 0 : Math.Min(_executionRoom, fillBudget);
            _statePool = _stateCount == 0 ? 0 : Math.Min(_stateRoom, fillBudget);
            // When the gas cap cannot cover both rooms, neither dimension is squeezed below half of the budget.
            _capLimited = (_executionCount > 0 && _executionPool < _executionRoom) || (_stateCount > 0 && _statePool < _stateRoom);
            if (_executionPool + _statePool > fillBudget)
            {
                _capLimited = true;
                ulong half = fillBudget / 2;
                if (_executionPool <= half) _statePool = fillBudget - _executionPool;
                else if (_statePool <= half) _executionPool = fillBudget - _statePool;
                else (_executionPool, _statePool) = (half, fillBudget - half);
            }
            return true;
        }

        public void ReserveEvenSplit()
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                ulong execution = _fillExecution[i] ? _executionPool / (ulong)_executionCount : _frames[i].ExecutionGasLimit;
                ulong state = _fillState[i] ? _statePool / (ulong)_stateCount : _frames[i].StateGasLimit;
                _reservations[i] = new FrameReservation { Execution = execution, State = state };
                _frames[i] = WithGas(_frames[i], execution, state);
            }
        }

        /// <summary>Probes the even split; later frames keep a margin over what they used on it, so their reservation
        /// absorbs usage that shifts as earlier frames are minimised.</summary>
        public void MeasureReservations()
        {
            Probe();
            // A payer that cannot afford the rooms at the requested fees never settles, so the search runs unpriced.
            if (_tracer.Receipts is null && IsPriced)
            {
                _unpricedSearch = true;
                Probe();
            }
            TxFrameReceipt[]? split = _tracer.Receipts;
            for (int i = 0; i < _frames.Length; i++)
            {
                ref FrameReservation reservation = ref _reservations[i];
                reservation.OutgrewSplit = split?[i].Status != TxFrameReceipt.StatusSuccess;
                if (reservation.OutgrewSplit) continue;
                if (_fillExecution[i]) reservation.Execution = Math.Min(reservation.Execution, split![i].ExecutionGasUsed.SaturatingAdd(split[i].ExecutionGasUsed));
                if (_fillState[i]) reservation.State = Math.Min(reservation.State, split![i].StateGasUsed.SaturatingAdd(split[i].StateGasUsed));
            }
        }

        public bool TryMinimizeFrames([NotNullWhen(false)] out string? error)
        {
            for (int i = 0; i < _frames.Length; i++)
            {
                if (!_fillExecution[i] && !_fillState[i]) continue;
                RaiseToUpperLimits(i);
                _reservationFailure = default;
                if (_probes < MaxFrameProbes - 1 && !TryProbe(i, atUpperLimits: true) && !TryRecoverUpperProbe(i, out error))
                    return false;
                if (_fillState[i]) Minimize(i, false, _receipts?[i]);
                if (_fillExecution[i]) Minimize(i, true, _receipts?[i]);
            }
            error = null;
            return true;
        }

        public bool FitsRooms() =>
            TryReserveBlockRooms(out _, out _)
            && FrameTxValidation.TryCalculateGasBudget(_tx, _spec, out _, out _, out ulong totalGas, estimateSignatureBytes: true)
            && totalGas <= _gasCap;

        /// <summary>Runs the filled transaction at its real fees, which decides the outcome.</summary>
        public Result<TxFrame[]> Verify(out bool executionReverted)
        {
            bool succeeded = Probe(verifying: true);
            executionReverted = !succeeded && LastProbeReverted;
            if (succeeded) return _frames;
            string error = ProbeError();
            return Result<TxFrame[]>.Fail(_probesExhausted
                ? $"frame gas search used all {MaxFrameProbes} probes before verifying every limit: {error}"
                : error);
        }

        /// <summary>Retries a failed upper probe of frame <paramref name="index"/> with more room, or reports the
        /// first probe's failure.</summary>
        private bool TryRecoverUpperProbe(int index, [NotNullWhen(false)] out string? error)
        {
            error = ProbeFailure(index);
            bool reverted = LastProbeReverted;
            bool outOfGas = _tracer.FrameError == EvmExceptionType.OutOfGas;
            if (_tracer.FailedFrame == index && (TryFreeingOutgrownRoom(index) || (outOfGas && TryRebalancing(index))))
            {
                error = null;
                return true;
            }
            LastProbeReverted = reverted;
            return false;
        }

        /// <summary>Probes frame <paramref name="index"/> with the later frames that failed the even split holding nothing.</summary>
        /// <remarks>Such a frame may have failed or been skipped there only because this one did, so its reservation
        /// measured nothing.</remarks>
        private bool TryFreeingOutgrownRoom(int index)
        {
            if (_probes >= MaxFrameProbes - 1 || !HoldsOutgrownRoom(index)) return false;
            RaiseToUpperLimits(index, freeOutgrown: true);
            return TryProbe(index, atUpperLimits: true);
        }

        /// <summary>Probes frame <paramref name="index"/> with its room split differently between the dimensions,
        /// moving the capacity it takes from one pool to the other.</summary>
        /// <remarks>Only the gas cap couples the pools, so this applies only when it binds. A failed probe does not say
        /// which dimension ran short, so both ends of the range are tried first, then split points between them and the
        /// current one; the minimisation that follows returns whatever the frame does not use.</remarks>
        private bool TryRebalancing(int index)
        {
            if (!_capLimited) return false;
            (ulong execution, ulong state) = RaiseToUpperLimits(index);
            ulong total = execution + state;
            ulong most = Math.Min(total, _executionRoom - (_executionPool - execution));
            ulong least = total - Math.Min(total, _stateRoom - (_statePool - state));
            if (!_fillState[index]) return most > execution && TryShift(index, execution, state, most);
            if (!_fillExecution[index]) return least < execution && TryShift(index, execution, state, least);
            if (most > execution && TryShift(index, execution, state, most)) return true;
            if (least < execution && TryShift(index, execution, state, least)) return true;
            for (int level = 1; level <= RebalanceLevels; level++)
            {
                ulong parts = 1UL << level;
                for (ulong part = 1; part < parts; part += 2)
                {
                    if (most > execution && TryShift(index, execution, state, execution + (most - execution) / parts * part)) return true;
                    if (least < execution && TryShift(index, execution, state, execution - (execution - least) / parts * part)) return true;
                }
            }
            return false;
        }

        /// <param name="execution">The execution room the upper probe left frame <paramref name="index"/>.</param>
        /// <param name="state">The state room the upper probe left frame <paramref name="index"/>.</param>
        /// <param name="shifted">The execution room to try instead, the state room taking the rest.</param>
        private bool TryShift(int index, ulong execution, ulong state, ulong shifted)
        {
            if (_probes >= MaxFrameProbes - 1) return false;
            ulong shiftedState = execution + state - shifted;
            TxFrame frame = _frames[index];
            _frames[index] = WithGas(frame, _fillExecution[index] ? shifted : frame.ExecutionGasLimit,
                _fillState[index] ? shiftedState : frame.StateGasLimit);
            if (!TryProbe(index, atUpperLimits: true)) return false;
            _executionPool = _executionPool - execution + shifted;
            _statePool = _statePool - state + shiftedState;
            return true;
        }

        private bool HoldsOutgrownRoom(int index)
        {
            for (int i = index + 1; i < _frames.Length; i++)
                if (Reserved(i, execution: true, freeOutgrown: true) < _reservations[i].Execution
                    || Reserved(i, execution: false, freeOutgrown: true) < _reservations[i].State)
                    return true;
            return false;
        }

        private ulong Reserved(int index, bool execution, bool freeOutgrown)
        {
            FrameReservation reservation = _reservations[index];
            return freeOutgrown && reservation.OutgrewSplit && (execution ? _fillExecution[index] : _fillState[index])
                ? 0
                : execution ? reservation.Execution : reservation.State;
        }

        private bool TryReserveBlockRooms(out ulong execution, out ulong state) =>
            FrameTxValidation.TryCalculateBlockGasReservations(_tx, _spec, out execution, out state, estimateSignatureBytes: true)
            && execution <= _executionCap && state <= _stateCap;

        private ulong FixedGas()
        {
            ulong fixedGas = FrameTxValidation.SignatureVerificationWorkGas(_tx);
            for (int i = 0; i < _frames.Length; i++)
                fixedGas = fixedGas.SaturatingAdd(_fillExecution[i] ? 0 : _frames[i].ExecutionGasLimit).SaturatingAdd(_fillState[i] ? 0 : _frames[i].StateGasLimit);
            return fixedGas;
        }

        // Frames before index hold their final limits and later frames their reservations; index takes the rest,
        // returned in both dimensions even where it has an explicit limit.
        private (ulong Execution, ulong State) RaiseToUpperLimits(int index, bool freeOutgrown = false)
        {
            ulong execution = _executionPool;
            ulong state = _statePool;
            for (int i = 0; i < _frames.Length; i++)
            {
                if (i == index) continue;
                ulong otherExecution = i < index ? _frames[i].ExecutionGasLimit : Reserved(i, execution: true, freeOutgrown);
                ulong otherState = i < index ? _frames[i].StateGasLimit : Reserved(i, execution: false, freeOutgrown);
                if (i > index)
                {
                    // An earlier frame that took freed room can leave less than the reservations; they yield, so the
                    // probe stays within the pools.
                    if (_fillExecution[i]) otherExecution = Math.Min(otherExecution, execution);
                    if (_fillState[i]) otherState = Math.Min(otherState, state);
                    _frames[i] = WithGas(_frames[i], otherExecution, otherState);
                }
                if (_fillExecution[i]) execution = Deduct(execution, otherExecution);
                if (_fillState[i]) state = Deduct(state, otherState);
            }
            _frames[index] = WithGas(_frames[index], _fillExecution[index] ? execution : _frames[index].ExecutionGasLimit,
                _fillState[index] ? state : _frames[index].StateGasLimit);
            return (execution, state);
        }

        // Seeded from the frame's measured use; without one, the first accepted probe supplies it.
        private void Minimize(int index, bool execution, TxFrameReceipt? measured)
        {
            // A receipt of the frame failing, which can be all there is once probes run out, bounds nothing it needs.
            if (measured?.Status != TxFrameReceipt.StatusSuccess) measured = null;
            TxFrame frame = _frames[index];
            ulong high = execution ? frame.ExecutionGasLimit : frame.StateGasLimit;
            ulong low = measured is null ? 0 : Math.Min(Used(measured, execution), high);
            ulong candidate = measured is null ? high / 2 : Optimistic(low, high, execution);
            // Out of probes: take a limit unverified; the final probe checks the whole assignment.
            if (_probes >= MaxFrameProbes - 1) (high, _probesExhausted) = (measured is null ? Unmeasured(index, execution, high) : candidate, true);
            for (int attempt = 0; attempt < 8 && _probes < MaxFrameProbes - 1; attempt++)
            {
                _frames[index] = WithGas(frame, execution ? candidate : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : candidate);
                if (TryProbe(index, atUpperLimits: false))
                {
                    high = candidate;
                    if (measured is null && _receipts is not null)
                    {
                        measured = _receipts[index];
                        low = Math.Max(low, Math.Min(Used(measured, execution), high));
                        candidate = Optimistic(low, high, execution);
                        if (candidate < high) continue;
                    }
                }
                else low = candidate + 1;
                if (low >= high || high - low <= high * (ulong)_errorMargin / 10000) break;
                candidate = low + (high - low) / 2;
            }
            _frames[index] = WithGas(frame, execution ? high : frame.ExecutionGasLimit, execution ? frame.StateGasLimit : high);
        }

        // Without a measurement, a frame keeps its reservation plus an even part of the slack among the frames still
        // to come that outgrew the even split, since only those may need more than their reservation.
        private ulong Unmeasured(int index, bool execution, ulong high)
        {
            ulong reserve = Math.Min(execution ? _reservations[index].Execution : _reservations[index].State, high);
            if (!_reservations[index].OutgrewSplit) return reserve;
            ulong outgrown = 0;
            for (int i = index; i < _frames.Length; i++)
                if (_reservations[i].OutgrewSplit && (execution ? _fillExecution[i] : _fillState[i])) outgrown++;
            return reserve + (high - reserve) / outgrown;
        }

        // Frames up to and including index must succeed. At its upper limits a later frame may fail, since it holds
        // only a reservation until its own turn and may react to the gas it has; a smaller limit must not change that.
        private bool TryProbe(int index, bool atUpperLimits)
        {
            bool succeeded = Probe();
            (int? Frame, EvmExceptionType? Error) failure = (_tracer.FailedFrame, _tracer.FrameError);
            bool reservationBound = failure.Frame > index && (atUpperLimits || failure == _reservationFailure);
            if (!succeeded && !reservationBound) return false;
            if (atUpperLimits) _reservationFailure = failure;
            _receipts = _tracer.Receipts;
            return true;
        }

        /// <summary>The error <see cref="TryMinimizeFrames"/> reports for a failed probe of frame <paramref name="index"/>.</summary>
        private readonly string ProbeFailure(int index) =>
            _capLimited && _tracer.FailedFrame == index && _tracer.FrameError == EvmExceptionType.OutOfGas
                ? CannotEstimateGasExceeded
                : ProbeError();

        private readonly bool IsPriced => !_tx.GasPrice.IsZero || !_tx.DecodedMaxFeePerGas.IsZero || !_baseFee.IsZero;

        /// <param name="verifying">Whether this is the final probe, which always runs at the requested fees.</param>
        /// <returns>Whether the probe ran every frame successfully.</returns>
        private bool Probe(bool verifying = false)
        {
            _token.ThrowIfCancellationRequested();
            _probes++;
            bool unpriced = _unpricedSearch && !verifying;
            Transaction probe = new();
            _tx.CopyTo(probe, copyHash: false);
            probe.GasLimit = FrameTxValidation.TotalGasLimit(_frames);
            if (unpriced)
            {
                probe.GasPrice = 0;
                probe.DecodedMaxFeePerGas = 0;
            }
            _probeHeader.GasUsed = 0;
            _probeHeader.BaseFeePerGas = unpriced ? UInt256.Zero : _baseFee;
            _tracer.Clear();
            _processor.SetBlockExecutionContext(in _probeContext);
            _lastResult = _processor.CallAndRestore(probe, _probeTracer);
            LastProbeReverted = _lastResult.TransactionExecuted && _tracer.FrameError == EvmExceptionType.Revert;
            return _lastResult.GetErrorMessage(_tracer.Error) is null && _tracer.FailedFrame is null && _tracer.Receipts is not null;
        }

        /// <summary>Describes why the last probe failed; formatted only when a failure is reported.</summary>
        private readonly string ProbeError() =>
            _lastResult.GetErrorMessage(_tracer.Error)
            ?? (_tracer.FailedFrame is { } index ? $"frame {index} failed: {_tracer.FrameError}" : "frame transaction simulation failed");
    }

    private sealed class FrameEstimateTracer : CallOutputTracer, IFrameTxReceiptTracer
    {
        public TxFrameReceipt[]? Receipts { get; private set; }
        public int? FailedFrame { get; private set; }
        public EvmExceptionType? FrameError { get; private set; }
        public void ReportFrameEnd(int frameIndex, EvmExceptionType? error)
        {
            if (error is not null && FailedFrame is null)
            {
                FailedFrame = frameIndex;
                FrameError = error;
            }
        }
        public void ReportFrameTxReceipt(Address payer, TxFrameReceipt[] frameReceipts) => Receipts = frameReceipts;

        public void Clear()
        {
            Reset();
            Receipts = null;
            FailedFrame = null;
            FrameError = null;
        }
    }
}
