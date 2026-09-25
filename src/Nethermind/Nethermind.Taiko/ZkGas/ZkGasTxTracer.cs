// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Transaction tracer that meters ZK gas for every opcode and precompile execution.
/// Charges the shared <see cref="ZkGasMeter"/> so that block-level limits can be enforced.
///
/// Nethermind's VM callback ordering for CALL/CREATE-family opcodes is:
///   StartOperation → ReportOperationRemainingGas → ReportAction → (child) → ReportActionEnd
///
/// This means ReportOperationRemainingGas fires BEFORE ReportAction, so we cannot know
/// at charge time whether a spawn opcode actually opened a child frame. To handle this
/// we defer charging for spawn opcodes until ReportAction (or the next StartOperation)
/// resolves whether child work was dispatched.
///
/// This mirrors alethia-reth's deferred_steps pattern in its ZkGasInspector.
///
/// A step that fails is charged what REVM had spent when it halted (see <see cref="RevmFailedStepGas"/>),
/// so every step is held until the VM either reports an error for it or moves on.
/// </summary>
public sealed class ZkGasTxTracer : TxTracer
{
    private readonly ZkGasMeter _meter;

    // Per-opcode step tracking
    private byte _currentOpcode;
    private ulong _currentGasStart;
    private bool _stepActive;
    private bool _stepJustEnded;

    // Step state needed to rebuild REVM's charge when the step fails
    private TraceStack _stack;
    private ulong _memorySize;
    private int _returnDataLength;
    private int _depth = -1;
    private bool[] _staticFrames = new bool[8];

    // Finished non-spawn step waiting for a possible error report
    private bool _hasPendingStep;
    private byte _pendingOpcode;
    private ulong _pendingGasBefore;
    private ulong _pendingGasDelta;

    // Deferred spawn opcode charging
    private bool _hasDeferredStep;
    private byte _deferredOpcode;
    private ulong _deferredGasBefore;
    private ulong _deferredGasDelta;
    private bool _deferredSpawned;
    private bool _deferredErrored;

    // Precompile tracking
    private bool _pendingPrecompile;
    private Address? _precompileAddress;
    private ulong _precompileGasStart;

    /// <summary>
    /// Creates a new ZK gas tracer backed by the provided meter.
    /// </summary>
    public ZkGasTxTracer(ZkGasMeter meter)
    {
        _meter = meter;
        IsTracingInstructions = true;
        IsTracingActions = true;
        IsTracingStack = true;
        IsTracingMemory = true;
        IsTracingReturnData = true;
    }

    /// <summary>
    /// Captures the opcode and pre-execution gas for the current step.
    /// Flushes any previously deferred spawn opcode charge first.
    /// </summary>
    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        FlushPendingStep();
        FlushDeferredStep();

        _currentOpcode = (byte)opcode;
        _currentGasStart = gas;
        _stepActive = true;
        _stepJustEnded = false;
        _depth = env.CallDepth;
        _stack = default;
        _memorySize = 0;
        _returnDataLength = 0;
    }

    public override void SetOperationStack(TraceStack stack) => _stack = stack;

    public override void SetOperationMemorySize(ulong newSize) => _memorySize = newSize;

    public override void SetOperationReturnData(ReadOnlyMemory<byte> returnData) => _returnDataLength = returnData.Length;

    /// <summary>
    /// Computes the gas consumed by the current opcode. For spawn opcodes, defers
    /// charging until we learn whether child work was dispatched. For all other
    /// opcodes, holds the charge until the next step, a frame end or an error report for this one.
    /// </summary>
    public override void ReportOperationRemainingGas(ulong gas)
    {
        // A report outside a step (a call result pushed on resume, or a halt after the step already
        // ended) must not let a following error be applied to the previous step.
        _stepJustEnded = _stepActive;
        if (!_stepActive)
            return;

        _stepActive = false;

        ulong rawGas = _currentGasStart.SaturatingSub(gas);

        if (IsSpawnOpcode(_currentOpcode))
        {
            // Defer: we don't yet know if this opcode will open a child frame.
            // Resolution happens in one of three places:
            //  1. ReportAction (success path, CALL or CREATE) → _deferredSpawned = true
            //  2. ReportOperationError (instruction error path)  → _deferredErrored = true
            //  3. Otherwise (CREATE/CREATE2 post-trace bail on collision)
            //     → treated as spawned at flush time.
            _hasDeferredStep = true;
            _deferredOpcode = _currentOpcode;
            _deferredGasBefore = _currentGasStart;
            _deferredGasDelta = rawGas;
            _deferredSpawned = false;
            _deferredErrored = false;
        }
        else
        {
            _hasPendingStep = true;
            _pendingOpcode = _currentOpcode;
            _pendingGasBefore = _currentGasStart;
            _pendingGasDelta = rawGas;
        }
    }

    /// <summary>
    /// Tracks call/create actions. Marks the deferred spawn opcode as spawned and
    /// captures precompile gas start for separate precompile charging.
    /// </summary>
    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        FlushPendingStep();

        // The new frame runs one level below the last traced opcode and is static if its parent was.
        int childDepth = _depth + 1;
        if (childDepth >= _staticFrames.Length)
        {
            Array.Resize(ref _staticFrames, Math.Max(childDepth + 1, _staticFrames.Length * 2));
        }

        _staticFrames[childDepth] = callType == ExecutionType.STATICCALL || (_depth >= 0 && _staticFrames[_depth]);

        // Mark the deferred step as having actually spawned child work
        if (_hasDeferredStep)
        {
            _deferredSpawned = true;
        }

        if (isPrecompileCall)
        {
            _pendingPrecompile = true;
            _precompileAddress = to;
            _precompileGasStart = gas;
        }
    }

    /// <summary>
    /// Charges precompile ZK gas when a precompile call completes successfully.
    /// Also flushes the deferred spawn step since the action is now resolved.
    /// </summary>
    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        FlushPendingStep();
        FlushDeferredStep();
        ChargePrecompileIfPending(gas);
    }

    /// <summary>
    /// Charges precompile ZK gas when a create-type action ends.
    /// Also flushes the deferred spawn step since the action is now resolved.
    /// </summary>
    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        FlushPendingStep();
        FlushDeferredStep();
        ChargePrecompileIfPending(gas);
    }

    /// <summary>
    /// Charges precompile ZK gas on action error.
    /// Also flushes the deferred spawn step.
    /// </summary>
    /// <remarks>
    /// The EVM specification burns all gas forwarded to a precompile when it errors (not reverts),
    /// so gas remaining after the call is zero. Calling <see cref="ChargePrecompileIfPending"/>
    /// with <c>gasRemaining = 0</c> therefore charges the full forwarded gas
    /// (<c>_precompileGasStart - 0 = _precompileGasStart</c>), which mirrors alethia-reth's
    /// <c>call_end</c> where <c>gas_used = inputs.gas_limit - outcome.result.gas.remaining()</c>
    /// evaluates to <c>gas_limit</c> on error.
    /// </remarks>
    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        FlushPendingStep();
        FlushDeferredStep();
        ChargePrecompileIfPending(gasRemaining: 0);
    }

    /// <summary>
    /// Replaces the measured gas of a failed step with what REVM (alethia-reth) had spent when it halted.
    /// </summary>
    /// <remarks>
    /// Nethermind reports zero gas left on any out-of-gas failure and validates stack, static context and
    /// operands in a different order, while REVM deducts the instruction table's static gas first and keeps
    /// any gas it had not charged yet. Since Unzen the per-step charge feeds the consensus-relevant
    /// <c>block.Header.Difficulty</c>, so it has to match exactly.
    /// </remarks>
    public override void ReportOperationError(EvmExceptionType error)
    {
        bool stepFailed = _stepJustEnded;
        _stepJustEnded = false;

        if (_hasDeferredStep)
        {
            // Depth and balance short-circuits push zero without failing the step; REVM runs its call hook
            // before those checks, so the step counts as spawned.
            if (error == EvmExceptionType.NotEnoughBalance)
            {
                _deferredSpawned = true;
                return;
            }

            // Remember that the just-deferred spawn op errored (e.g. OOG between
            // EndInstructionTrace and child-frame dispatch). At flush time this
            // suppresses the post-trace-bail "treat as spawned" path for CREATE/CREATE2.
            _deferredErrored = true;
            if (stepFailed)
            {
                _deferredGasDelta = _deferredGasBefore - RevmGasAfter(_deferredOpcode, error, _deferredGasBefore, _deferredGasBefore - _deferredGasDelta);
            }

            return;
        }

        if (_hasPendingStep && stepFailed)
        {
            _pendingGasDelta = _pendingGasBefore - RevmGasAfter(_pendingOpcode, error, _pendingGasBefore, _pendingGasBefore - _pendingGasDelta);
            FlushPendingStep();
        }
    }

    private ulong RevmGasAfter(byte opcode, EvmExceptionType error, ulong gasBefore, ulong reportedGasAfter)
    {
        bool isStatic = _depth >= 0 && _depth < _staticFrames.Length && _staticFrames[_depth];
        return Math.Min(gasBefore, RevmFailedStepGas.GasAfter((Instruction)opcode, error, gasBefore, reportedGasAfter,
            _stack, _memorySize, _returnDataLength, isStatic));
    }

    /// <summary>
    /// Charges precompile ZK gas on revert.
    /// Also flushes the deferred spawn step.
    /// </summary>
    public override void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output)
    {
        FlushPendingStep();
        FlushDeferredStep();
        ChargePrecompileIfPending(gas);
    }

    private void FlushPendingStep()
    {
        if (!_hasPendingStep)
            return;

        _hasPendingStep = false;
        _meter.ChargeOpcode(_pendingOpcode, _pendingGasDelta);
    }

    /// <summary>
    /// Flushes any deferred spawn opcode charge.
    ///
    /// Charge selection:
    ///  - <c>_deferredSpawned</c> set by ReportAction (child frame dispatched) → spawn estimate.
    ///  - Else if CREATE/CREATE2 reached ReportOperationRemainingGas without erroring,
    ///    it must have been a post-trace bail (address collision);
    ///    REVM treats this as spawned too → spawn estimate.
    ///  - Otherwise (CALL-family with no dispatch, or any spawn op that errored mid-flight)
    ///    → measured raw gas delta.
    /// </summary>
    private void FlushDeferredStep()
    {
        if (!_hasDeferredStep)
            return;

        _hasDeferredStep = false;

        bool isCreate = _deferredOpcode == 0xf0 || _deferredOpcode == 0xf5;
        bool treatAsSpawned = _deferredSpawned || (isCreate && !_deferredErrored);

        ulong rawGas = treatAsSpawned
            ? GetSpawnEstimate(_deferredOpcode)
            : _deferredGasDelta;

        _meter.ChargeOpcode(_deferredOpcode, rawGas);
    }

    private void ChargePrecompileIfPending(ulong gasRemaining)
    {
        if (!_pendingPrecompile)
            return;

        _pendingPrecompile = false;
        ulong gasUsed = _precompileGasStart.SaturatingSub(gasRemaining);
        if (gasUsed > 0 && _precompileAddress is not null)
        {
            _meter.ChargePrecompile(_precompileAddress, gasUsed);
        }

        _precompileAddress = null;
    }

    private static bool IsSpawnOpcode(byte opcode) =>
        opcode is 0xf0 // CREATE
            or 0xf1    // CALL
            or 0xf2    // CALLCODE
            or 0xf4    // DELEGATECALL
            or 0xf5    // CREATE2
            or 0xfa;   // STATICCALL

    private static ulong GetSpawnEstimate(byte opcode) => opcode switch
    {
        0xf1 => ZkGasSchedule.SpawnEstimateCall,
        0xf2 => ZkGasSchedule.SpawnEstimateCallCode,
        0xf4 => ZkGasSchedule.SpawnEstimateDelegateCall,
        0xfa => ZkGasSchedule.SpawnEstimateStaticCall,
        0xf0 => ZkGasSchedule.SpawnEstimateCreate,
        0xf5 => ZkGasSchedule.SpawnEstimateCreate2,
        _ => 0
    };
}
