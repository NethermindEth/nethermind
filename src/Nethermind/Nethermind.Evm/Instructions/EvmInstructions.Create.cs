// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Int256;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;

internal sealed partial class EvmInstructions
{
    public interface IOpCreate
    {
        abstract static ExecutionType ExecutionType { get; }
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCreate<TOpCreate>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpCreate : struct, IOpCreate
    {
        Metrics.IncrementCreates();

        IReleaseSpec spec = vm.Spec;
        if (vm.EvmState.IsStatic) goto StaticCallViolation;

        vm.ReturnData = null;
        ref readonly ExecutionEnvironment env = ref vm.EvmState.Env;
        IWorldState state = vm.WorldState;

        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
        if (!state.AccountExists(env.ExecutingAccount))
        {
            state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
        }

        if (!stack.PopUInt256(out UInt256 value) ||
            !stack.PopUInt256(out UInt256 memoryPositionOfInitCode) ||
            !stack.PopUInt256(out UInt256 initCodeLength))
            goto StackUnderflow;

        Span<byte> salt = default;
        if (typeof(TOpCreate) == typeof(OpCreate2))
        {
            salt = stack.PopWord256();
        }

        //EIP-3860
        if (spec.IsEip3860Enabled)
        {
            if (initCodeLength > spec.MaxInitCodeSize) goto OutOfGas;
        }

        long gasCost = GasCostOf.Create +
                       (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(in initCodeLength) : 0) +
                       (typeof(TOpCreate) == typeof(OpCreate2)
                           ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in initCodeLength)
                           : 0);

        if (!UpdateGas(gasCost, ref gasAvailable)) goto OutOfGas;

        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in memoryPositionOfInitCode, in initCodeLength)) goto OutOfGas;

        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
        {
            // TODO: need a test for this
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            goto None;
        }

        ReadOnlyMemory<byte> initCode = vm.EvmState.Memory.Load(in memoryPositionOfInitCode, in initCodeLength);

        UInt256 balance = state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            goto None;
        }

        UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            goto None;
        }

        if (vm.TxTracer.IsTracingInstructions) vm.EndInstructionTrace(gasAvailable);
        // todo: === below is a new call - refactor / move

        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) goto OutOfGas;

        Address contractAddress = typeof(TOpCreate) == typeof(OpCreate)
            ? ContractAddress.From(env.ExecutingAccount, state.GetNonce(env.ExecutingAccount))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        if (spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vm.EvmState.AccessTracker.WarmUp(contractAddress);
        }

        // Do not add the initCode to the cache as it is
        // pointing to data in this tx and will become invalid
        // for another tx as returned to pool.
        if (spec.IsEofEnabled && initCode.Span.StartsWith(EofValidator.MAGIC))
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            UpdateGasUp(callGas, ref gasAvailable);
            goto None;
        }

        state.IncrementNonce(env.ExecutingAccount);

        CodeInfoFactory.CreateInitCodeInfo(initCode.ToArray(), spec, out ICodeInfo codeinfo, out _);
        codeinfo.AnalyseInBackgroundIfRequired();

        Snapshot snapshot = state.TakeSnapshot();

        bool accountExists = state.AccountExists(contractAddress);

        if (accountExists && contractAddress.IsNonZeroAccount(spec, vm.CodeInfoRepository, state))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            //if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Contract collision at {contractAddress}");
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            goto None;
        }

        if (state.IsDeadAccount(contractAddress))
        {
            state.ClearStorage(contractAddress);
        }

        state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeinfo,
            inputData: default,
            transferValue: value,
            value: value
        );
        vm.ReturnData = EvmState.RentFrame(
            callGas,
            outputDestination: 0,
            outputLength: 0,
            TOpCreate.ExecutionType,
            isStatic: vm.EvmState.IsStatic,
            isCreateOnPreExistingAccount: accountExists,
            in snapshot,
            env: in callEnv,
            in vm.EvmState.AccessTracker
        );
    None:
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    public struct OpCreate : IOpCreate
    {
        public static ExecutionType ExecutionType => ExecutionType.CREATE;
    }

    public struct OpCreate2 : IOpCreate
    {
        public static ExecutionType ExecutionType => ExecutionType.CREATE2;
    }
}
