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

    // Precompile tracking
    private bool _pendingPrecompile;
    private byte _precompileAddressByte;
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
    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
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
            // ReportAction will mark it as spawned if it does.
            _hasDeferredStep = true;
            _deferredOpcode = _currentOpcode;
            _deferredGasDelta = rawGas;
            _deferredSpawned = false;
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
            _precompileAddressByte = to.Bytes[19];
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
    /// Clears pending precompile state on action error.
    /// Also flushes the deferred spawn step.
    /// </summary>
    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        FlushDeferredStep();

        if (_pendingPrecompile)
        {
            _meter.ChargePrecompile(_precompileAddressByte, (ulong)_precompileGasStart);
            _pendingPrecompile = false;
        }
    }

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
    /// Flushes any deferred spawn opcode charge. If the opcode was marked as spawned
    /// (by ReportAction), uses the fixed spawn estimate; otherwise uses the measured
    /// gas delta.
    /// </summary>
    private void FlushDeferredStep()
    {
        if (!_hasDeferredStep)
            return;

        _hasDeferredStep = false;

        ulong rawGas = _deferredSpawned
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
        if (gasUsed > 0)
        {
            _meter.ChargePrecompile(_precompileAddressByte, (ulong)gasUsed);
        }
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
