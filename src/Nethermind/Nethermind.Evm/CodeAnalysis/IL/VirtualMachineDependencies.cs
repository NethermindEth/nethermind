// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Evm.Precompiles;
using System.Runtime.CompilerServices;
using Nethermind.Int256;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.CodeAnalysis.IL
{
    public static class VirtualMachineDependencies
    {
        public static bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        public static void UpdateGasUp(long refund, ref long gasAvailable)
        {
            gasAvailable += refund;
        }

        public static bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, bool chargeForDelegation, IWorldState state, IReleaseSpec spec, bool chargeForWarm = true)
        {
            if (!spec.UseHotAndColdStorage)
            {
                return true;
            }
            bool notOutOfGas = ChargeAccountGas(ref gasAvailable, vmState, address, spec);
            return notOutOfGas
                   && (!chargeForDelegation
                       || !vmState.Env.TxExecutionContext.CodeInfoRepository.TryGetDelegation(state, address, out Address delegated)
                       || ChargeAccountGas(ref gasAvailable, vmState, delegated, spec));

            bool ChargeAccountGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec)
            {
                bool result = true;

                if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec))
                {
                    result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                    vmState.AccessTracker.WarmUp(address);
                }
                else if (chargeForWarm)
                {
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
                return result;
            }
        }

        public enum StorageAccessType
        {
            SLOAD,
            SSTORE
        }

        public static bool ChargeStorageAccessGas(
            ref long gasAvailable,
            EvmState vmState,
            in StorageCell storageCell,
            StorageAccessType storageAccessType,
            IReleaseSpec spec)
        {
            // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                if (vmState.AccessTracker.IsCold(in storageCell))
                {
                    result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                    vmState.AccessTracker.WarmUp(in storageCell);
                }
                else if (storageAccessType == StorageAccessType.SLOAD)
                {
                    // we do not charge for WARM_STORAGE_READ_COST in SSTORE scenario
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
        }
        public static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false) =>
            instruction switch
            {
                Instruction.CALL => ExecutionType.CALL,
                Instruction.DELEGATECALL => ExecutionType.DELEGATECALL,
                Instruction.STATICCALL => ExecutionType.STATICCALL,
                Instruction.CALLCODE => ExecutionType.CALLCODE,
                _ => throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}")
            };

        [SkipLocalsInit]
        public static EvmExceptionType InstructionCall(
            EvmState vmState, IWorldState state, ref long gasAvailable, IReleaseSpec spec,
            Instruction instruction,
            UInt256 gasLimit,
            Address codeSource,
            UInt256 callValue,
            UInt256 dataOffset,
            UInt256 dataLength,
            UInt256 outputOffset,
            UInt256 outputLength,
            out UInt256? toPushInStack,
            ref ReadOnlyMemory<byte> returnBuffer,
            out object returnData)
        {
            returnData = null;
            toPushInStack = null;

            ref readonly ExecutionEnvironment env = ref vmState.Env;

            Metrics.IncrementCalls();

            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource, true, state, spec)) return EvmExceptionType.OutOfGas;

            UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;

            if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE) return EvmExceptionType.StaticCallViolation;

            Address caller = instruction == Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;

            Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL
                ? codeSource
                : env.ExecutingAccount;

            long gasExtra = 0L;

            if (!transferValue.IsZero)
            {
                gasExtra += GasCostOf.CallValue;
            }

            if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
            {
                gasExtra += GasCostOf.NewAccount;
            }
            else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
            {
                gasExtra += GasCostOf.NewAccount;
            }

            if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
                !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
                !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
                !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

            CodeInfo codeInfo = vmState.Env.TxExecutionContext.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec, out _);
            codeInfo.AnalyseInBackgroundIfRequired();

            if (spec.Use63Over64Rule)
            {
                gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
            }

            if (gasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

            long gasLimitUl = gasLimit.ToLong();
            if (!UpdateGas(gasLimitUl, ref gasAvailable)) return EvmExceptionType.OutOfGas;

            if (!transferValue.IsZero)
            {
                gasLimitUl += GasCostOf.CallStipend;
            }

            if (env.CallDepth >= VirtualMachine.MaxCallDepth || (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
            {
                returnBuffer = Array.Empty<byte>();
                toPushInStack = UInt256.Zero;

                UpdateGasUp(gasLimitUl, ref gasAvailable);
                return EvmExceptionType.None;
            }

            Snapshot snapshot = state.TakeSnapshot();
            state.SubtractFromBalance(caller, transferValue, spec);

            if (codeInfo.IsEmpty)
            {
                // Non contract call, no need to construct call frame can just credit balance and return gas
                returnBuffer = default;
                toPushInStack = StatusCode.Success;

                UpdateGasUp(gasLimitUl, ref gasAvailable);
                return FastCall(spec, out returnData, in transferValue, target);
            }

            ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);
            ExecutionEnvironment callEnv = new
            (
                txExecutionContext: in env.TxExecutionContext,
                callDepth: env.CallDepth + 1,
                caller: caller,
                codeSource: codeSource,
                executingAccount: target,
                transferValue: transferValue,
                value: callValue,
                inputData: callData,
                codeInfo: codeInfo
            );
            if (outputLength == 0)
            {
                // TODO: when output length is 0 outputOffset can have any value really
                // and the value does not matter and it can cause trouble when beyond long range
                outputOffset = 0;
            }

            ExecutionType executionType = GetCallExecutionType(instruction, env.IsPostMerge());
            returnData = EvmState.RentFrame(
                gasLimitUl,
                outputOffset.ToLong(),
                outputLength.ToLong(),
                executionType,
                instruction == Instruction.STATICCALL || vmState.IsStatic,
                isCreateOnPreExistingAccount: false,
                snapshot: snapshot,
                env: callEnv,
                stateForAccessLists: vmState.AccessTracker);

            return EvmExceptionType.None;

            EvmExceptionType FastCall(IReleaseSpec spec, out object returnData, in UInt256 transferValue, Address target)
            {
                state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
                Metrics.IncrementEmptyCalls();

                returnData = CallResult.BoxedEmpty;
                return EvmExceptionType.None;
            }
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionSelfDestruct(EvmState vmState, IWorldState state, Address inheritor, ref long gasAvailable, IReleaseSpec spec)
        {
            Metrics.IncrementSelfDestructs();

            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, false, state, spec, false)) return EvmExceptionType.OutOfGas;

            Address executingAccount = vmState.Env.ExecutingAccount;
            bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
            if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
                vmState.AccessTracker.ToBeDestroyed(executingAccount);

            UInt256 result = state.GetBalance(executingAccount);
            if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
            {
                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }

            bool inheritorAccountExists = state.AccountExists(inheritor);
            if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
            {
                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }

            if (!inheritorAccountExists)
            {
                state.CreateAccount(inheritor, result);
            }
            else if (!inheritor.Equals(executingAccount))
            {
                state.AddToBalance(inheritor, result, spec);
            }

            if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
                return EvmExceptionType.None; // don't burn eth when contract is not destroyed per EIP clarification

            state.SubtractFromBalance(executingAccount, result, spec);
            return EvmExceptionType.None;
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionCreate(
            EvmState vmState, IWorldState state, ref long gasAvailable, IReleaseSpec spec, Instruction instruction,
            UInt256 value, UInt256 memoryPositionOfInitCode, UInt256 initCodeLength, Span<byte> salt,
            out UInt256? statusReturn,
            ref ReadOnlyMemory<byte> returnDataBuffer,
            out object callState)
        {
            ref readonly ExecutionEnvironment env = ref vmState.Env;
            callState = null;
            statusReturn = null;

            // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
            if (!state.AccountExists(env.ExecutingAccount))
            {
                state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
            }

            //EIP-3860
            if (spec.IsEip3860Enabled)
            {
                if (initCodeLength > spec.MaxInitCodeSize) return EvmExceptionType.OutOfGas;
            }

            bool outOfGas = false;
            long gasCost = (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength, out outOfGas) : 0) +
                           (instruction == Instruction.CREATE2
                               ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength, out outOfGas)
                               : 0);
            if (outOfGas || !UpdateGas(gasCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

            if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) return EvmExceptionType.OutOfGas;

            // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
            if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
            {
                // TODO: need a test for this
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                return EvmExceptionType.None;
            }

            ReadOnlyMemory<byte> initCode = vmState.Memory.Load(in memoryPositionOfInitCode, initCodeLength);

            UInt256 balance = state.GetBalance(env.ExecutingAccount);
            if (value > balance)
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                return EvmExceptionType.None;
            }

            UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
            UInt256 maxNonce = ulong.MaxValue;
            if (accountNonce >= maxNonce)
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                return EvmExceptionType.None;
            }


            long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
            if (!UpdateGas(callGas, ref gasAvailable)) return EvmExceptionType.OutOfGas;

            Address contractAddress = instruction == Instruction.CREATE
                ? ContractAddress.From(env.ExecutingAccount, state.GetNonce(env.ExecutingAccount))
                : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

            if (spec.UseHotAndColdStorage)
            {
                // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
                vmState.AccessTracker.WarmUp(contractAddress);
            }

            state.IncrementNonce(env.ExecutingAccount);

            Snapshot snapshot = state.TakeSnapshot();

            bool accountExists = state.AccountExists(contractAddress);

            if (accountExists && contractAddress.IsNonZeroAccount(spec, env.TxExecutionContext.CodeInfoRepository, state))
            {
                /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                return EvmExceptionType.None;
            }

            if (state.IsDeadAccount(contractAddress))
            {
                state.ClearStorage(contractAddress);
            }

            state.SubtractFromBalance(env.ExecutingAccount, value, spec);

            // Do not add the initCode to the cache as it is
            // pointing to data in this tx and will become invalid
            // for another tx as returned to pool.
            CodeInfo codeInfo = new(initCode);
            codeInfo.AnalyseInBackgroundIfRequired();

            ExecutionEnvironment callEnv = new
            (
                txExecutionContext: in env.TxExecutionContext,
                callDepth: env.CallDepth + 1,
                caller: env.ExecutingAccount,
                executingAccount: contractAddress,
                codeSource: null,
                codeInfo: codeInfo,
                inputData: default,
                transferValue: value,
                value: value
            );

            callState = EvmState.RentFrame(
                callGas,
                0L,
                0L,
                instruction == Instruction.CREATE2 ? ExecutionType.CREATE2 : ExecutionType.CREATE,
                vmState.IsStatic,
                accountExists,
                snapshot,
                callEnv,
                vmState.AccessTracker);

            return EvmExceptionType.None;
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionSLoad<TTracingInstructions, TTracingStorage>(EvmState vmState, IWorldState state, UInt256 index, ref long gasAvailable, IReleaseSpec spec, out UInt256 value)
            where TTracingInstructions : struct, IIsTracing
            where TTracingStorage : struct, IIsTracing
        {
            value = default;

            Metrics.IncrementSLoadOpcode();
            gasAvailable -= spec.GetSLoadCost();

            StorageCell storageCell = new(vmState.Env.ExecutingAccount, index);
            if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vmState,
                in storageCell,
                StorageAccessType.SLOAD,
                spec)) return EvmExceptionType.OutOfGas;

            value = new UInt256(state.Get(in storageCell));

            return EvmExceptionType.None;
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionSStore(EvmState vmState, IWorldState state, ref long gasAvailable, ref UInt256 result, ref ReadOnlySpan<byte> bytes, IReleaseSpec spec)
        {
            Metrics.IncrementSStoreOpcode();

            // fail fast before the first storage read if gas is not enough even for reset
            if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

            if (spec.UseNetGasMeteringWithAStipendFix)
            {
                if (gasAvailable <= GasCostOf.CallStipend) return EvmExceptionType.OutOfGas;
            }

            bool newIsZero = bytes.IsZero();
            bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

            StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

            if (!ChargeStorageAccessGas(
                    ref gasAvailable,
                    vmState,
                    in storageCell,
                    StorageAccessType.SSTORE,
                    spec)) return EvmExceptionType.OutOfGas;

            ReadOnlySpan<byte> currentValue = state.Get(in storageCell);
            // Console.WriteLine($"current: {currentValue.ToHexString()} newValue {newValue.ToHexString()}");
            bool currentIsZero = currentValue.IsZero();

            bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, bytes);
            long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

            if (!spec.UseNetGasMetering) // note that for this case we already deducted 5000
            {
                if (newIsZero)
                {
                    if (!newSameAsCurrent)
                    {
                        vmState.Refund += sClearRefunds;
                    }
                }
                else if (currentIsZero)
                {
                    if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) return EvmExceptionType.OutOfGas;
                }
            }
            else // net metered
            {
                if (newSameAsCurrent)
                {
                    if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;
                }
                else // net metered, C != N
                {
                    Span<byte> originalValue = state.GetOriginal(in storageCell);
                    bool originalIsZero = originalValue.IsZero();

                    bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                    if (currentSameAsOriginal)
                    {
                        if (currentIsZero)
                        {
                            if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) return EvmExceptionType.OutOfGas;
                        }
                        else // net metered, current == original != new, !currentIsZero
                        {
                            if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

                            if (newIsZero)
                            {
                                vmState.Refund += sClearRefunds;
                            }
                        }
                    }
                    else // net metered, new != current != original
                    {
                        long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                        if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

                        if (!originalIsZero) // net metered, new != current != original != 0
                        {
                            if (currentIsZero)
                            {
                                vmState.Refund -= sClearRefunds;
                            }

                            if (newIsZero)
                            {
                                vmState.Refund += sClearRefunds;
                            }
                        }

                        bool newSameAsOriginal = Bytes.AreEqual(originalValue, bytes);
                        if (newSameAsOriginal)
                        {
                            long refundFromReversal;
                            if (originalIsZero)
                            {
                                refundFromReversal = spec.GetSetReversalRefund();
                            }
                            else
                            {
                                refundFromReversal = spec.GetClearReversalRefund();
                            }

                            vmState.Refund += refundFromReversal;
                        }
                    }
                }
            }

            if (!newSameAsCurrent)
            {
                state.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
            }

            return EvmExceptionType.None;
        }

        public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
        {
            long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
            if (outOfGas) return false;
            return memoryCost == 0L || UpdateGas(memoryCost, ref gasAvailable);
        }
    }
}
