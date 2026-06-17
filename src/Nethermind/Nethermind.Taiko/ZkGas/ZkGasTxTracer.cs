// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
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
/// </summary>
public sealed class ZkGasTxTracer : TxTracer
{
    private readonly ZkGasMeter _meter;

    // Per-opcode step tracking
    private byte _currentOpcode;
    private long _currentGasStart;
    private bool _stepActive;

    // Deferred spawn opcode charging
    private bool _hasDeferredStep;
    private byte _deferredOpcode;
    private ulong _deferredGasDelta;
    private bool _deferredSpawned;
    private bool _deferredErrored;

    // Precompile tracking
    private bool _pendingPrecompile;
    private Address? _precompileAddress;
    private long _precompileGasStart;

    /// <summary>
    /// Creates a new ZK gas tracer backed by the provided meter.
    /// </summary>
    public ZkGasTxTracer(ZkGasMeter meter)
    {
        _meter = meter;
        IsTracingInstructions = true;
        IsTracingActions = true;
    }

    /// <summary>
    /// Captures the opcode and pre-execution gas for the current step.
    /// Flushes any previously deferred spawn opcode charge first.
    /// </summary>
    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        FlushDeferredStep();

        _currentOpcode = (byte)opcode;
        _currentGasStart = gas;
        _stepActive = true;
    }

    /// <summary>
    /// Computes the gas consumed by the current opcode. For spawn opcodes, defers
    /// charging until we learn whether child work was dispatched. For all other
    /// opcodes, charges immediately.
    /// </summary>
    public override void ReportOperationRemainingGas(long gas)
    {
        if (!_stepActive)
            return;

        _stepActive = false;

        long delta = _currentGasStart - gas;
        ulong rawGas = delta > 0 ? (ulong)delta : 0;

        if (IsSpawnOpcode(_currentOpcode))
        {
            // Defer: we don't yet know if this opcode will open a child frame.
            // Resolution happens in one of three places:
            //  1. ReportAction (success path, CALL or CREATE) → _deferredSpawned = true
            //  2. ReportOperationError (instruction error path)  → _deferredErrored = true
            //  3. Otherwise (CREATE/CREATE2 post-trace bail on EIP-7610 collision)
            //     → treated as spawned at flush time.
            _hasDeferredStep = true;
            _deferredOpcode = _currentOpcode;
            _deferredGasDelta = rawGas;
            _deferredSpawned = false;
            _deferredErrored = false;
        }
        else
        {
            _meter.ChargeOpcode(_currentOpcode, rawGas);
        }
    }

    /// <summary>
    /// Tracks call/create actions. Marks the deferred spawn opcode as spawned and
    /// captures precompile gas start for separate precompile charging.
    /// </summary>
    public override void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
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
    public override void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
    {
        FlushDeferredStep();
        ChargePrecompileIfPending(gas);
    }

    /// <summary>
    /// Charges precompile ZK gas when a create-type action ends.
    /// Also flushes the deferred spawn step since the action is now resolved.
    /// </summary>
    public override void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
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
        FlushDeferredStep();
        ChargePrecompileIfPending(gasRemaining: 0);
    }

    /// <summary>
    /// Adjusts ZK gas for opcodes that fail the static-context check. REVM (used by alethia-reth)
    /// charges the opcode's static gas cost in its main interpreter loop BEFORE dispatching to the
    /// instruction handler — so a failed TSTORE/SSTORE/LOG/CREATE/SELFDESTRUCT in a static frame
    /// has its constant gas cost burned. Nethermind's handlers check static first and return
    /// without consuming the constant gas, so the measured gas delta in
    /// <see cref="ReportOperationRemainingGas"/> is 0.
    ///
    /// For mainnet semantics this is invisible (the entire failed call frame's gas is consumed
    /// regardless), but for Taiko ZK gas accounting the per-opcode gas delta drives the
    /// consensus-relevant block.Header.Difficulty value, so we must match REVM's behaviour
    /// exactly. Charge the missing constant gas here so the resulting zk gas equals what
    /// alethia-reth produces.
    /// </summary>
    public override void ReportOperationError(EvmExceptionType error)
    {
        // Remember that the just-deferred spawn op errored (e.g. OOG between
        // EndInstructionTrace and child-frame dispatch). At flush time this
        // suppresses the post-trace-bail "treat as spawned" path for CREATE/CREATE2.
        if (_hasDeferredStep)
        {
            _deferredErrored = true;
        }

        if (error == EvmExceptionType.StaticCallViolation)
        {
            ulong staticGas = GetRevmStaticGasForOpcode(_currentOpcode);
            if (staticGas > 0)
            {
                _meter.ChargeOpcode(_currentOpcode, staticGas);
            }
        }
    }

    /// <summary>
    /// REVM's per-opcode constant gas cost charged in the interpreter loop before dispatch.
    /// Source: revm-interpreter <c>instructions.rs</c> instruction-table entries.
    /// Only opcodes that can fail the static-context check are listed; other opcodes never
    /// reach <see cref="EvmExceptionType.StaticCallViolation"/>.
    /// </summary>
    private static ulong GetRevmStaticGasForOpcode(byte opcode) => opcode switch
    {
        0x5d => 100,    // TSTORE — WarmStorageReadCostEIP2929 (EIP-1153)
        0x55 => 0,      // SSTORE — entirely dynamic, no constant
        0xa0 => 375,    // LOG0 — base only; topics/data dynamic
        0xa1 => 375,    // LOG1
        0xa2 => 375,    // LOG2
        0xa3 => 375,    // LOG3
        0xa4 => 375,    // LOG4
        0xf0 => 0,      // CREATE — REVM dispatches with 0 static gas; revm-interpreter::contract::create handles internally
        0xf5 => 0,      // CREATE2
        0xff => 5000,   // SELFDESTRUCT — base before refund logic
        _ => 0,
    };

    /// <summary>
    /// Charges precompile ZK gas on revert.
    /// Also flushes the deferred spawn step.
    /// </summary>
    public override void ReportActionRevert(long gas, ReadOnlyMemory<byte> output)
    {
        FlushDeferredStep();
        ChargePrecompileIfPending(gas);
    }

    /// <summary>
    /// Flushes any deferred spawn opcode charge.
    ///
    /// Charge selection:
    ///  - <c>_deferredSpawned</c> set by ReportAction (child frame dispatched) → spawn estimate.
    ///  - Else if CREATE/CREATE2 reached ReportOperationRemainingGas without erroring,
    ///    it must have been a post-trace bail (EIP-7610 collision);
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

    private void ChargePrecompileIfPending(long gasRemaining)
    {
        if (!_pendingPrecompile)
            return;

        _pendingPrecompile = false;
        long gasUsed = _precompileGasStart - gasRemaining;
        if (gasUsed > 0 && _precompileAddress is not null)
        {
            _meter.ChargePrecompile(_precompileAddress, (ulong)gasUsed);
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
