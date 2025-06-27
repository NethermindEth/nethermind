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
using static Nethermind.Evm.EvmInstructions;
using Nethermind.Evm.EvmObjectFormat;

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

        private static bool ChargeAccountAccessGasWithDelegation(ref long gasAvailable, ICodeInfoRepository codeInfoRepository, IWorldState state, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true)
        {
            if (!spec.UseHotAndColdStorage)
            {
                // No extra cost if hot/cold storage is not used.
                return true;
            }
            bool notOutOfGas = ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec, chargeForWarm);
            return notOutOfGas
                   && (!codeInfoRepository.TryGetDelegation(state, address, spec, out Address delegated)
                       // Charge additional gas for the delegated account if it exists.
                       || ChargeAccountAccessGas(ref gasAvailable, vmState, delegated, spec, chargeForWarm));
        }

        private static bool ChargeForLargeContractAccess(uint excessContractSize, Address codeAddress, in StackAccessTracker accessTracer, ref long gasAvailable)
        {
            if (accessTracer.WarmUpLargeContract(codeAddress))
            {
                long largeContractCost = GasCostOf.InitCodeWord * EvmInstructions.Div32Ceiling(excessContractSize, out bool outOfGas);
                if (outOfGas || !UpdateGas(largeContractCost, ref gasAvailable)) return false;
            }

            return true;
        }

        public static bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true)
        {
            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                // If the account is cold (and not a precompile), charge the cold access cost.
                if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec))
                {
                    result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                    vmState.AccessTracker.WarmUp(address);
                }
                else if (chargeForWarm)
                {
                    // Otherwise, if warm access should be charged, apply the warm read cost.
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
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
            EvmState vmState,
            ICodeInfoRepository codeInfoRepository,
            IWorldState state,
            ref long gasAvailable, IReleaseSpec spec,
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

            Metrics.IncrementCalls();

            // Charge gas for accessing the account's code (including delegation logic if applicable).
            if (!ChargeAccountAccessGasWithDelegation(ref gasAvailable, codeInfoRepository, state, vmState, codeSource, spec)) goto OutOfGas;

            ref readonly ExecutionEnvironment env = ref vmState.Env;
            // Determine the call value based on the call type.

            // For non-delegate calls, the transfer value is the call value.
            UInt256 transferValue = instruction is Instruction.DELEGATECALL ? UInt256.Zero : callValue;
            // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
            if (vmState.IsStatic && !transferValue.IsZero && instruction is Instruction.CALL)
                return EvmExceptionType.StaticCallViolation;

            // Determine caller and target based on the call type.
            Address caller = instruction is Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;
            Address target = (instruction is Instruction.CALL || instruction is Instruction.STATICCALL)
                ? codeSource
                : env.ExecutingAccount;

            long gasExtra = 0L;

            // Add extra gas cost if value is transferred.
            if (!transferValue.IsZero)
            {
                gasExtra += GasCostOf.CallValue;
            }

            // Charge additional gas if the target account is new or considered empty.
            if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
            {
                gasExtra += GasCostOf.NewAccount;
            }
            else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
            {
                gasExtra += GasCostOf.NewAccount;
            }


            // Update gas: call cost, memory expansion for input and output, and extra gas.
            if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
                !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
                !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
                !UpdateGas(gasExtra, ref gasAvailable))
                goto OutOfGas;

            // Retrieve code information for the call and schedule background analysis if needed.
            ICodeInfo codeInfo = codeInfoRepository.GetCachedCodeInfo(state, codeSource, spec);

            // If contract is large, charge for access
            if (spec.IsEip7907Enabled)
            {
                uint excessContractSize = (uint)Math.Max(0, codeInfo.Code.Length - CodeSizeConstants.MaxCodeSizeEip170);
                if (excessContractSize > 0 && !ChargeForLargeContractAccess(excessContractSize, codeSource, in vmState.AccessTracker, ref gasAvailable))
                    goto OutOfGas;
            }
            // Apply the 63/64 gas rule if enabled.
            if (spec.Use63Over64Rule)
            {
                gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
            }

            // If gasLimit exceeds the host's representable range, treat as out-of-gas.
            if (gasLimit >= long.MaxValue) goto OutOfGas;

            long gasLimitUl = (long)gasLimit;
            if (!UpdateGas(gasLimitUl, ref gasAvailable)) goto OutOfGas;

            // Add call stipend if value is being transferred.
            if (!transferValue.IsZero)
            {
                gasLimitUl += GasCostOf.CallStipend;
            }

            // Check call depth and balance of the caller.
            if (env.CallDepth >= MaxCallDepth ||
                (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
            {
                // If the call cannot proceed, return an empty response and push zero on the stack.
                returnBuffer = Array.Empty<byte>();
                toPushInStack = UInt256.Zero;

                // Refund the remaining gas to the caller.
                UpdateGasUp(gasLimitUl, ref gasAvailable);
                return EvmExceptionType.None;
            }

            // Take a snapshot of the state for potential rollback.
            Snapshot snapshot = state.TakeSnapshot();
            // Subtract the transfer value from the caller's balance.
            state.SubtractFromBalance(caller, in transferValue, spec);

            // Fast-path for calls to externally owned accounts (non-contracts)
            if (codeInfo.IsEmpty)
            {
                toPushInStack = new UInt256(StatusCode.SuccessBytes.Span);
                UpdateGasUp(gasLimitUl, ref gasAvailable);
                return FastCall(state, spec, in transferValue, target, out returnBuffer);
            }

            // Load call data from memory.
            ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);
            // Construct the execution environment for the call.
            ExecutionEnvironment callEnv = new(
                codeInfo: codeInfo,
                executingAccount: target,
                caller: caller,
                codeSource: codeSource,
                callDepth: env.CallDepth + 1,
                transferValue: in transferValue,
                value: in callValue,
                inputData: in callData);

            // Normalize output offset if output length is zero.
            if (outputLength == 0)
            {
                // Output offset is inconsequential when output length is 0.
                outputOffset = 0;
            }

            // Rent a new call frame for executing the call.
            returnData = EvmState.RentFrame(
                gasAvailable: gasLimitUl,
                outputDestination: outputOffset.ToLong(),
                outputLength: outputLength.ToLong(),
                executionType: GetCallExecutionType(instruction),
                isStatic: instruction is Instruction.STATICCALL or Instruction.EXTSTATICCALL || vmState.IsStatic,
                isCreateOnPreExistingAccount: false,
                env: in callEnv,
                stateForAccessLists: in vmState.AccessTracker,
                snapshot: in snapshot);

            return EvmExceptionType.None;

            // Fast-call path for non-contract calls:
            // Directly credit the target account and avoid constructing a full call frame.
            static EvmExceptionType FastCall(IWorldState state, IReleaseSpec spec, in UInt256 transferValue, Address target, out ReadOnlyMemory<byte> returnBuffer)
            {
                state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
                Metrics.IncrementEmptyCalls();

                returnBuffer = null;
                return EvmExceptionType.None;
            }

        // Jump forward to be unpredicted by the branch predictor.
        OutOfGas:
            return EvmExceptionType.OutOfGas;
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionSelfDestruct(EvmState vmState, IWorldState state, Address inheritor, ref long gasAvailable, IReleaseSpec spec)
        {
            Metrics.IncrementSelfDestructs();

            // SELFDESTRUCT is forbidden during static calls.
            if (vmState.IsStatic)
                goto StaticCallViolation;

            // If Shanghai DDoS protection is active, charge the appropriate gas cost.
            if (spec.UseShanghaiDDosProtection)
            {
                gasAvailable -= GasCostOf.SelfDestructEip150;
            }

            // Charge gas for account access; if insufficient, signal out-of-gas.
            if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, spec, chargeForWarm: false))
                goto OutOfGas;

            Address executingAccount = vmState.Env.ExecutingAccount;
            bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
            // Mark the executing account for destruction if allowed.
            if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
                vmState.AccessTracker.ToBeDestroyed(executingAccount);

            // Retrieve the current balance for transfer.
            UInt256 result = state.GetBalance(executingAccount);

            // For certain specs, charge gas if transferring to a dead account.
            if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
            {
                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                    goto OutOfGas;
            }

            // If account creation rules apply, ensure gas is charged for new accounts.
            bool inheritorAccountExists = state.AccountExists(inheritor);
            if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
            {
                if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                    goto OutOfGas;
            }

            // Create or update the inheritor account with the transferred balance.
            if (!inheritorAccountExists)
            {
                state.CreateAccount(inheritor, result);
            }
            else if (!inheritor.Equals(executingAccount))
            {
                state.AddToBalance(inheritor, result, spec);
            }

            // Special handling when SELFDESTRUCT is limited to the same transaction.
            if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
                goto Stop; // Avoid burning ETH if contract is not destroyed per EIP clarification

            // Subtract the balance from the executing account.
            state.SubtractFromBalance(executingAccount, result, spec);

        // Jump forward to be unpredicted by the branch predictor.
        Stop:
            return EvmExceptionType.Stop;
        OutOfGas:
            return EvmExceptionType.OutOfGas;
        StaticCallViolation:
            return EvmExceptionType.StaticCallViolation;
        }

        [SkipLocalsInit]
        public static EvmExceptionType InstructionCreate(
            EvmState vmState, IWorldState state, ICodeInfoRepository codeInfoRepository, ref long gasAvailable, IReleaseSpec spec, Instruction instruction,
            UInt256 value, UInt256 memoryPositionOfInitCode, UInt256 initCodeLength, Span<byte> salt,
            out UInt256? statusReturn,
            ref ReadOnlyMemory<byte> returnDataBuffer,
            out object callState)
        {
            callState = null;
            statusReturn = null;

            // Increment metrics counter for contract creation operations.
            Metrics.IncrementCreates();

            // Obtain the current EVM specification and check if the call is static (static calls cannot create contracts).
            if (vmState.IsStatic)
                goto StaticCallViolation;

            // Reset the return data buffer as contract creation does not use previous return data.
            ref readonly ExecutionEnvironment env = ref vmState.Env;

            // Ensure the executing account exists in the world state. If not, create it with a zero balance.
            if (!state.AccountExists(env.ExecutingAccount))
            {
                state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
            }

            // Pop parameters off the stack: value to transfer, memory position for the initialization code,
            // and the length of the initialization code.

            // EIP-3860: Limit the maximum size of the initialization code.
            if (spec.IsEip3860Enabled)
            {
                if (initCodeLength > spec.MaxInitCodeSize)
                    goto OutOfGas;
            }

            bool outOfGas = false;
            // Calculate the gas cost for the creation, including fixed cost and per-word cost for init code.
            // Also include an extra cost for CREATE2 if applicable.
            long gasCost = GasCostOf.Create +
                           (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmInstructions.Div32Ceiling(in initCodeLength, out outOfGas) : 0) +
                           (instruction == Instruction.CREATE2
                               ? GasCostOf.Sha3Word * EvmInstructions.Div32Ceiling(in initCodeLength, out outOfGas)
                               : 0);

            // Check gas sufficiency: if outOfGas flag was set during gas division or if gas update fails.
            if (outOfGas || !UpdateGas(gasCost, ref gasAvailable))
                goto OutOfGas;

            // Update memory gas cost based on the required memory expansion for the init code.
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, in initCodeLength))
                goto OutOfGas;

            // Verify call depth does not exceed the maximum allowed. If exceeded, return early with empty data.
            // This guard ensures we do not create nested contract calls beyond EVM limits.
            if (env.CallDepth >= MaxCallDepth)
            {
                returnDataBuffer = default;
                statusReturn = UInt256.Zero;
                goto None;
            }

            // Load the initialization code from memory based on the specified position and length.
            ReadOnlyMemory<byte> initCode = vmState.Memory.Load(in memoryPositionOfInitCode, in initCodeLength);

            // Check that the executing account has sufficient balance to transfer the specified value.
            UInt256 balance = state.GetBalance(env.ExecutingAccount);
            if (value > balance)
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                goto None;
            }

            // Retrieve the nonce of the executing account to ensure it hasn't reached the maximum.
            UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
            UInt256 maxNonce = ulong.MaxValue;
            if (accountNonce >= maxNonce)
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                goto None;
            }

            // Calculate gas available for the contract creation call.
            // Use the 63/64 gas rule if specified in the current EVM specification.
            long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
            if (!UpdateGas(callGas, ref gasAvailable))
                goto OutOfGas;

            // Compute the contract address:
            // - For CREATE: based on the executing account and its current nonce.
            // - For CREATE2: based on the executing account, the provided salt, and the init code.
            Address contractAddress = instruction is Instruction.CREATE
                ? ContractAddress.From(env.ExecutingAccount, state.GetNonce(env.ExecutingAccount))
                : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

            // For EIP-2929 support, pre-warm the contract address in the access tracker to account for hot/cold storage costs.
            if (spec.UseHotAndColdStorage)
            {
                vmState.AccessTracker.WarmUp(contractAddress);
            }

            // Special case: if EOF code format is enabled and the init code starts with the EOF marker,
            // the creation is not executed. This ensures that a special marker is not mistakenly executed as code.
            if (spec.IsEofEnabled && initCode.Span.StartsWith(EofValidator.MAGIC))
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                UpdateGasUp(callGas, ref gasAvailable);
                goto None;
            }

            // Increment the nonce of the executing account to reflect the contract creation.
            state.IncrementNonce(env.ExecutingAccount);

            // Analyze and compile the initialization code.
            CodeInfoFactory.CreateInitCodeInfo(initCode.ToArray(), spec, out ICodeInfo? codeInfo, out _);

            // Take a snapshot of the current state. This allows the state to be reverted if contract creation fails.
            Snapshot snapshot = state.TakeSnapshot();

            // Check for contract address collision. If the contract already exists and contains code or non-zero state,
            // then the creation should be aborted.
            bool accountExists = state.AccountExists(contractAddress);
            if (accountExists && contractAddress.IsNonZeroAccount(spec, codeInfoRepository, state))
            {
                returnDataBuffer = Array.Empty<byte>();
                statusReturn = UInt256.Zero;
                goto None;
            }

            // If the contract address refers to a dead account, clear its storage before creation.
            if (state.IsDeadAccount(contractAddress))
            {
                state.ClearStorage(contractAddress);
            }

            // Deduct the transfer value from the executing account's balance.
            state.SubtractFromBalance(env.ExecutingAccount, value, spec);

            // Construct a new execution environment for the contract creation call.
            // This environment sets up the call frame for executing the contract's initialization code.
            ExecutionEnvironment callEnv = new(
                codeInfo: codeInfo,
                executingAccount: contractAddress,
                caller: env.ExecutingAccount,
                codeSource: null,
                callDepth: env.CallDepth + 1,
                transferValue: in value,
                value: in value,
                inputData: in EvmInstructions._emptyMemory);

            // Rent a new frame to run the initialization code in the new execution environment.
            callState = EvmState.RentFrame(
                gasAvailable: callGas,
                outputDestination: 0,
                outputLength: 0,
                executionType: instruction is Instruction.CREATE ? ExecutionType.CREATE : ExecutionType.CREATE2,
                isStatic: vmState.IsStatic,
                isCreateOnPreExistingAccount: accountExists,
                env: in callEnv,
                stateForAccessLists: in vmState.AccessTracker,
                snapshot: in snapshot);
        None:
            return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
        OutOfGas:
            return EvmExceptionType.OutOfGas;
        StaticCallViolation:
            return EvmExceptionType.StaticCallViolation;
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
