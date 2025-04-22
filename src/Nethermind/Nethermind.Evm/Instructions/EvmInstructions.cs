// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;
using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;
using Int256;
using Nethermind.Evm.Precompiles;

internal static unsafe partial class EvmInstructions
{
    /// <summary>
    /// Generates the opcode lookup table for the Ethereum Virtual Machine.
    /// Each of the 256 entries in the returned array corresponds to an EVM instruction,
    /// with unassigned opcodes defaulting to a bad instruction handler.
    /// </summary>
    /// <typeparam name="TTracingInstructions">A struct implementing IFlag used for tracing purposes.</typeparam>
    /// <param name="spec">The release specification containing enabled features and opcode flags.</param>
    /// <returns>An array of function pointers (opcode handlers) indexed by opcode value.</returns>
    public static OpCode[] GenerateOpCodes<TTracingInstructions>(IReleaseSpec spec)
        where TTracingInstructions : struct, IFlag
    {
        // Allocate lookup table for all possible opcodes.
        OpCode[] lookup = new OpCode[byte.MaxValue + 1];

        for (int i = 0; i < lookup.Length; i++)
        {
            lookup[i] = &InstructionBadInstruction;
        }

        // Set basic control and arithmetic opcodes.
        lookup[(int)Instruction.STOP] = &InstructionStop;
        lookup[(int)Instruction.ADD] = &InstructionMath2Param<OpAdd>;
        lookup[(int)Instruction.MUL] = &InstructionMath2Param<OpMul>;
        lookup[(int)Instruction.SUB] = &InstructionMath2Param<OpSub>;
        lookup[(int)Instruction.DIV] = &InstructionMath2Param<OpDiv>;
        lookup[(int)Instruction.SDIV] = &InstructionMath2Param<OpSDiv>;
        lookup[(int)Instruction.MOD] = &InstructionMath2Param<OpMod>;
        lookup[(int)Instruction.SMOD] = &InstructionMath2Param<OpSMod>;
        lookup[(int)Instruction.ADDMOD] = &InstructionMath3Param<OpAddMod>;
        lookup[(int)Instruction.MULMOD] = &InstructionMath3Param<OpMulMod>;
        lookup[(int)Instruction.EXP] = &InstructionExp;
        lookup[(int)Instruction.SIGNEXTEND] = &InstructionSignExtend;

        // Comparison and bitwise opcodes.
        lookup[(int)Instruction.LT] = &InstructionMath2Param<OpLt>;
        lookup[(int)Instruction.GT] = &InstructionMath2Param<OpGt>;
        lookup[(int)Instruction.SLT] = &InstructionMath2Param<OpSLt>;
        lookup[(int)Instruction.SGT] = &InstructionMath2Param<OpSGt>;
        lookup[(int)Instruction.EQ] = &InstructionBitwise<OpBitwiseEq>;
        lookup[(int)Instruction.ISZERO] = &InstructionMath1Param<OpIsZero>;
        lookup[(int)Instruction.AND] = &InstructionBitwise<OpBitwiseAnd>;
        lookup[(int)Instruction.OR] = &InstructionBitwise<OpBitwiseOr>;
        lookup[(int)Instruction.XOR] = &InstructionBitwise<OpBitwiseXor>;
        lookup[(int)Instruction.NOT] = &InstructionMath1Param<OpNot>;
        lookup[(int)Instruction.BYTE] = &InstructionByte;

        // Conditional: enable shift opcodes if the spec allows.
        if (spec.ShiftOpcodesEnabled)
        {
            lookup[(int)Instruction.SHL] = &InstructionShift<OpShl>;
            lookup[(int)Instruction.SHR] = &InstructionShift<OpShr>;
            lookup[(int)Instruction.SAR] = &InstructionSar;
        }

        // Cryptographic hash opcode.
        lookup[(int)Instruction.KECCAK256] = &InstructionKeccak256;

        // Environment opcodes.
        lookup[(int)Instruction.ADDRESS] = &InstructionEnvBytes<OpAddress>;
        lookup[(int)Instruction.BALANCE] = &InstructionBalance;
        lookup[(int)Instruction.ORIGIN] = &InstructionEnvBytes<OpOrigin>;
        lookup[(int)Instruction.CALLER] = &InstructionEnvBytes<OpCaller>;
        lookup[(int)Instruction.CALLVALUE] = &InstructionEnvUInt256<OpCallValue>;
        lookup[(int)Instruction.CALLDATALOAD] = &InstructionCallDataLoad;
        lookup[(int)Instruction.CALLDATASIZE] = &InstructionEnvUInt32<OpCallDataSize>;
        lookup[(int)Instruction.CALLDATACOPY] = &InstructionCodeCopy<OpCallDataCopy, TTracingInstructions>;
        lookup[(int)Instruction.CODESIZE] = &InstructionEnvUInt32<OpCodeSize>;
        lookup[(int)Instruction.CODECOPY] = &InstructionCodeCopy<OpCodeCopy, TTracingInstructions>;
        lookup[(int)Instruction.GASPRICE] = &InstructionEnvUInt256<OpGasPrice>;

        lookup[(int)Instruction.EXTCODESIZE] = &InstructionExtCodeSize<TTracingInstructions>;
        lookup[(int)Instruction.EXTCODECOPY] = &InstructionExtCodeCopy<TTracingInstructions>;

        // Return data opcodes (if enabled).
        if (spec.ReturnDataOpcodesEnabled)
        {
            lookup[(int)Instruction.RETURNDATASIZE] = &InstructionReturnDataSize;
            lookup[(int)Instruction.RETURNDATACOPY] = &InstructionReturnDataCopy<TTracingInstructions>;
        }

        // Extended code hash opcode handling.
        if (spec.ExtCodeHashOpcodeEnabled)
        {
            lookup[(int)Instruction.EXTCODEHASH] = spec.IsEofEnabled ? &InstructionExtCodeHashEof : &InstructionExtCodeHash;
        }

        lookup[(int)Instruction.BLOCKHASH] = &InstructionBlockHash;

        // More environment opcodes.
        lookup[(int)Instruction.COINBASE] = &InstructionEnvBytes<OpCoinbase>;
        lookup[(int)Instruction.TIMESTAMP] = &InstructionEnvUInt64<OpTimestamp>;
        lookup[(int)Instruction.NUMBER] = &InstructionEnvUInt64<OpNumber>;
        lookup[(int)Instruction.PREVRANDAO] = &InstructionPrevRandao;
        lookup[(int)Instruction.GASLIMIT] = &InstructionEnvUInt64<OpGasLimit>;
        if (spec.ChainIdOpcodeEnabled)
        {
            lookup[(int)Instruction.CHAINID] = &InstructionChainId;
        }
        if (spec.SelfBalanceOpcodeEnabled)
        {
            lookup[(int)Instruction.SELFBALANCE] = &InstructionSelfBalance;
        }
        if (spec.BaseFeeEnabled)
        {
            lookup[(int)Instruction.BASEFEE] = &InstructionEnvUInt256<OpBaseFee>;
        }
        if (spec.IsEip4844Enabled)
        {
            lookup[(int)Instruction.BLOBHASH] = &InstructionBlobHash;
        }
        if (spec.BlobBaseFeeEnabled)
        {
            lookup[(int)Instruction.BLOBBASEFEE] = &InstructionEnvUInt256<OpBlobBaseFee>;
        }

        // Gap: opcodes 0x4b to 0x4f are unassigned.

        // Memory and storage instructions.
        lookup[(int)Instruction.POP] = &InstructionPop;
        lookup[(int)Instruction.MLOAD] = &InstructionMLoad<TTracingInstructions>;
        lookup[(int)Instruction.MSTORE] = &InstructionMStore<TTracingInstructions>;
        lookup[(int)Instruction.MSTORE8] = &InstructionMStore8<TTracingInstructions>;
        lookup[(int)Instruction.SLOAD] = &InstructionSLoad;
        lookup[(int)Instruction.SSTORE] = &InstructionSStore<TTracingInstructions>;

        // Jump instructions.
        lookup[(int)Instruction.JUMP] = &InstructionJump;
        lookup[(int)Instruction.JUMPI] = &InstructionJumpIf;
        lookup[(int)Instruction.PC] = &InstructionProgramCounter;
        lookup[(int)Instruction.MSIZE] = &InstructionEnvUInt64<OpMSize>;
        lookup[(int)Instruction.GAS] = &InstructionGas;
        lookup[(int)Instruction.JUMPDEST] = &InstructionJumpDest;

        // Transient storage opcodes.
        if (spec.TransientStorageEnabled)
        {
            lookup[(int)Instruction.TLOAD] = &InstructionTLoad;
            lookup[(int)Instruction.TSTORE] = &InstructionTStore;
        }
        if (spec.MCopyIncluded)
        {
            lookup[(int)Instruction.MCOPY] = &InstructionMCopy<TTracingInstructions>;
        }

        // Optional PUSH0 instruction.
        if (spec.IncludePush0Instruction)
        {
            lookup[(int)Instruction.PUSH0] = &InstructionPush0;
        }

        // PUSH opcodes (PUSH1 to PUSH32).
        lookup[(int)Instruction.PUSH1] = &InstructionPush<Op1>;
        lookup[(int)Instruction.PUSH2] = &InstructionPush2<TTracingInstructions>;
        lookup[(int)Instruction.PUSH3] = &InstructionPush<Op3>;
        lookup[(int)Instruction.PUSH4] = &InstructionPush<Op4>;
        lookup[(int)Instruction.PUSH5] = &InstructionPush<Op5>;
        lookup[(int)Instruction.PUSH6] = &InstructionPush<Op6>;
        lookup[(int)Instruction.PUSH7] = &InstructionPush<Op7>;
        lookup[(int)Instruction.PUSH8] = &InstructionPush<Op8>;
        lookup[(int)Instruction.PUSH9] = &InstructionPush<Op9>;
        lookup[(int)Instruction.PUSH10] = &InstructionPush<Op10>;
        lookup[(int)Instruction.PUSH11] = &InstructionPush<Op11>;
        lookup[(int)Instruction.PUSH12] = &InstructionPush<Op12>;
        lookup[(int)Instruction.PUSH13] = &InstructionPush<Op13>;
        lookup[(int)Instruction.PUSH14] = &InstructionPush<Op14>;
        lookup[(int)Instruction.PUSH15] = &InstructionPush<Op15>;
        lookup[(int)Instruction.PUSH16] = &InstructionPush<Op16>;
        lookup[(int)Instruction.PUSH17] = &InstructionPush<Op17>;
        lookup[(int)Instruction.PUSH18] = &InstructionPush<Op18>;
        lookup[(int)Instruction.PUSH19] = &InstructionPush<Op19>;
        lookup[(int)Instruction.PUSH20] = &InstructionPush<Op20>;
        lookup[(int)Instruction.PUSH21] = &InstructionPush<Op21>;
        lookup[(int)Instruction.PUSH22] = &InstructionPush<Op22>;
        lookup[(int)Instruction.PUSH23] = &InstructionPush<Op23>;
        lookup[(int)Instruction.PUSH24] = &InstructionPush<Op24>;
        lookup[(int)Instruction.PUSH25] = &InstructionPush<Op25>;
        lookup[(int)Instruction.PUSH26] = &InstructionPush<Op26>;
        lookup[(int)Instruction.PUSH27] = &InstructionPush<Op27>;
        lookup[(int)Instruction.PUSH28] = &InstructionPush<Op28>;
        lookup[(int)Instruction.PUSH29] = &InstructionPush<Op29>;
        lookup[(int)Instruction.PUSH30] = &InstructionPush<Op30>;
        lookup[(int)Instruction.PUSH31] = &InstructionPush<Op31>;
        lookup[(int)Instruction.PUSH32] = &InstructionPush<Op32>;

        // DUP opcodes (DUP1 to DUP16).
        lookup[(int)Instruction.DUP1] = &InstructionDup<Op1>;
        lookup[(int)Instruction.DUP2] = &InstructionDup<Op2>;
        lookup[(int)Instruction.DUP3] = &InstructionDup<Op3>;
        lookup[(int)Instruction.DUP4] = &InstructionDup<Op4>;
        lookup[(int)Instruction.DUP5] = &InstructionDup<Op5>;
        lookup[(int)Instruction.DUP6] = &InstructionDup<Op6>;
        lookup[(int)Instruction.DUP7] = &InstructionDup<Op7>;
        lookup[(int)Instruction.DUP8] = &InstructionDup<Op8>;
        lookup[(int)Instruction.DUP9] = &InstructionDup<Op9>;
        lookup[(int)Instruction.DUP10] = &InstructionDup<Op10>;
        lookup[(int)Instruction.DUP11] = &InstructionDup<Op11>;
        lookup[(int)Instruction.DUP12] = &InstructionDup<Op12>;
        lookup[(int)Instruction.DUP13] = &InstructionDup<Op13>;
        lookup[(int)Instruction.DUP14] = &InstructionDup<Op14>;
        lookup[(int)Instruction.DUP15] = &InstructionDup<Op15>;
        lookup[(int)Instruction.DUP16] = &InstructionDup<Op16>;

        // SWAP opcodes (SWAP1 to SWAP16).
        lookup[(int)Instruction.SWAP1] = &InstructionSwap<Op1>;
        lookup[(int)Instruction.SWAP2] = &InstructionSwap<Op2>;
        lookup[(int)Instruction.SWAP3] = &InstructionSwap<Op3>;
        lookup[(int)Instruction.SWAP4] = &InstructionSwap<Op4>;
        lookup[(int)Instruction.SWAP5] = &InstructionSwap<Op5>;
        lookup[(int)Instruction.SWAP6] = &InstructionSwap<Op6>;
        lookup[(int)Instruction.SWAP7] = &InstructionSwap<Op7>;
        lookup[(int)Instruction.SWAP8] = &InstructionSwap<Op8>;
        lookup[(int)Instruction.SWAP9] = &InstructionSwap<Op9>;
        lookup[(int)Instruction.SWAP10] = &InstructionSwap<Op10>;
        lookup[(int)Instruction.SWAP11] = &InstructionSwap<Op11>;
        lookup[(int)Instruction.SWAP12] = &InstructionSwap<Op12>;
        lookup[(int)Instruction.SWAP13] = &InstructionSwap<Op13>;
        lookup[(int)Instruction.SWAP14] = &InstructionSwap<Op14>;
        lookup[(int)Instruction.SWAP15] = &InstructionSwap<Op15>;
        lookup[(int)Instruction.SWAP16] = &InstructionSwap<Op16>;

        // LOG opcodes.
        lookup[(int)Instruction.LOG0] = &InstructionLog<Op0>;
        lookup[(int)Instruction.LOG1] = &InstructionLog<Op1>;
        lookup[(int)Instruction.LOG2] = &InstructionLog<Op2>;
        lookup[(int)Instruction.LOG3] = &InstructionLog<Op3>;
        lookup[(int)Instruction.LOG4] = &InstructionLog<Op4>;

        // Extended opcodes for EO (EoF) mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.DATALOAD] = &InstructionDataLoad;
            lookup[(int)Instruction.DATALOADN] = &InstructionDataLoadN;
            lookup[(int)Instruction.DATASIZE] = &InstructionDataSize;
            lookup[(int)Instruction.DATACOPY] = &InstructionDataCopy<TTracingInstructions>;
            lookup[(int)Instruction.RJUMP] = &InstructionRelativeJump;
            lookup[(int)Instruction.RJUMPI] = &InstructionRelativeJumpIf;
            lookup[(int)Instruction.RJUMPV] = &InstructionJumpTable;
            lookup[(int)Instruction.CALLF] = &InstructionCallFunction;
            lookup[(int)Instruction.RETF] = &InstructionReturnFunction;
            lookup[(int)Instruction.JUMPF] = &InstructionJumpFunction;
            lookup[(int)Instruction.DUPN] = &InstructionDupN;
            lookup[(int)Instruction.SWAPN] = &InstructionSwapN;
            lookup[(int)Instruction.EXCHANGE] = &InstructionExchange;
            lookup[(int)Instruction.EOFCREATE] = &InstructionEofCreate<TTracingInstructions>;
            lookup[(int)Instruction.RETURNCODE] = &InstructionReturnCode;
        }

        // Contract creation and call opcodes.
        lookup[(int)Instruction.CREATE] = &InstructionCreate<OpCreate, TTracingInstructions>;
        lookup[(int)Instruction.CALL] = &InstructionCall<OpCall, TTracingInstructions>;
        lookup[(int)Instruction.CALLCODE] = &InstructionCall<OpCallCode, TTracingInstructions>;
        lookup[(int)Instruction.RETURN] = &InstructionReturn;
        if (spec.DelegateCallEnabled)
        {
            lookup[(int)Instruction.DELEGATECALL] = &InstructionCall<OpDelegateCall, TTracingInstructions>;
        }
        if (spec.Create2OpcodeEnabled)
        {
            lookup[(int)Instruction.CREATE2] = &InstructionCreate<OpCreate2, TTracingInstructions>;
        }

        lookup[(int)Instruction.RETURNDATALOAD] = &InstructionReturnDataLoad;
        if (spec.StaticCallEnabled)
        {
            lookup[(int)Instruction.STATICCALL] = &InstructionCall<OpStaticCall, TTracingInstructions>;
        }

        // Extended call opcodes in EO mode.
        if (spec.IsEofEnabled)
        {
            lookup[(int)Instruction.EXTCALL] = &InstructionEofCall<OpEofCall, TTracingInstructions>;
            if (spec.DelegateCallEnabled)
            {
                lookup[(int)Instruction.EXTDELEGATECALL] = &InstructionEofCall<OpEofDelegateCall, TTracingInstructions>;
            }
            if (spec.StaticCallEnabled)
            {
                lookup[(int)Instruction.EXTSTATICCALL] = &InstructionEofCall<OpEofStaticCall, TTracingInstructions>;
            }
        }

        if (spec.RevertOpcodeEnabled)
        {
            lookup[(int)Instruction.REVERT] = &InstructionRevert;
        }

        // Final opcodes.
        lookup[(int)Instruction.INVALID] = &InstructionInvalid;
        lookup[(int)Instruction.SELFDESTRUCT] = &InstructionSelfDestruct;

        return lookup;
    }

    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    private static bool ChargeAccountAccessGasWithDelegation(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true, Instruction opCode = Instruction.STOP)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
        {
            // No extra cost if hot/cold storage is not used.
            return true;
        }
        bool notOutOfGas = ChargeAccountAccessGas(ref gasAvailable, vm, address, chargeForWarm, opCode);
        return notOutOfGas
               && (!vm.EvmState.Env.TxExecutionContext.CodeInfoRepository.TryGetDelegation(vm.WorldState, address, spec, out Address delegated)
                   // Charge additional gas for the delegated account if it exists.
                   || ChargeAccountAccessGas(ref gasAvailable, vm, delegated, chargeForWarm, opCode));
    }

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <param name="opCode"></param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    private static bool ChargeAccountAccessGas(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true, Instruction opCode = Instruction.STOP)
    {
        bool result = true;
        bool witnessGasCharged = false;
        IReleaseSpec spec = vm.Spec;
        EvmState vmState = vm.EvmState;
        if (spec.IsEip4762Enabled)
        {
            bool isAddressPreCompile = address.IsPrecompile(spec);
            bool isSystemContract = address.IsSystemContract(spec);
            switch (opCode)
            {
                case Instruction.BALANCE:
                    {
                        var gasBefore = gasAvailable;
                        result = vmState.Env.Witness.AccessForBalanceOpCode(address, ref gasAvailable);
                        witnessGasCharged = gasBefore != gasAvailable;
                        break;
                    }
                case Instruction.EXTCODESIZE:
                case Instruction.EXTCODECOPY:
                case Instruction.CALL:
                case Instruction.CALLCODE:
                case Instruction.DELEGATECALL:
                case Instruction.STATICCALL:
                    {
                        if (!isAddressPreCompile && !isSystemContract)
                        {
                            var gasBefore = gasAvailable;
                            result = vmState.Env.Witness.AccessAccountData(address, ref gasAvailable);
                            witnessGasCharged = gasBefore != gasAvailable;
                        }

                        break;
                    }
                case Instruction.EXTCODEHASH:
                    {
                        if (!isAddressPreCompile && !isSystemContract)
                        {
                            var gasBefore = gasAvailable;
                            result = vmState.Env.Witness.AccessCodeHash(address, ref gasAvailable);
                            witnessGasCharged = gasBefore != gasAvailable;
                        }
                        break;
                    }
            }

            // we still use the UseHotAndColdStorage costs - we should be removing the Cold costs as it's being replaced
            // by the witness access costs. We should still be keeping the Hot costs - to avoid free repeated access and
            // cause a DDOS attack.
            if (!result) return false;
        }
        if (spec.UseHotAndColdStorage)
        {
            if (vm.TxTracer.IsTracingAccess)
            {
                // Ensure that tracing simulates access-list behavior.
                vmState.AccessTracker.WarmUp(address);
            }

            if (witnessGasCharged)
            {
                vmState.AccessTracker.WarmUp(address);
            }
            // If the account is cold (and not a precompile), charge the cold access cost.
            else  if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec) && !address.IsSystemContract(spec))
            {
                result = spec.IsEip4762Enabled || UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
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

    /// <summary>
    /// Charges the appropriate gas cost for accessing a storage cell, taking into account whether the access is cold or warm.
    /// <para>
    /// For cold storage accesses (or if not previously warmed up), a higher gas cost is applied. For warm accesses during SLOAD,
    /// a lower cost is deducted.
    /// </para>
    /// </summary>
    /// <param name="gasAvailable">The remaining gas, passed by reference and reduced by the access cost.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="storageCell">The target storage cell being accessed.</param>
    /// <param name="storageAccessType">Indicates whether the access is for a load (SLOAD) or store (SSTORE) operation.</param>
    /// <param name="spec">The release specification which governs gas metering and storage access rules.</param>
    /// <returns><c>true</c> if the gas charge was successfully applied; otherwise, <c>false</c> indicating an out-of-gas condition.</returns>
    private static bool ChargeStorageAccessGas(
        ref long gasAvailable,
        VirtualMachine vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        EvmState vmState = vm.EvmState;

        var gasBefore = gasAvailable;
        if (!vmState.Env.Witness.AccessForStorage(storageCell.Address, storageCell.Index,
                storageAccessType == StorageAccessType.SSTORE, ref gasAvailable))
        {
            return false;
        }
        bool witnessGasCharged = gasBefore != gasAvailable;

        bool result = true;

        // If the spec requires hot/cold storage tracking, determine if extra gas should be charged.
        if (spec.UseHotAndColdStorage)
        {
            // When tracing access, ensure the storage cell is marked as warm to simulate inclusion in the access list.
            ref readonly StackAccessTracker accessTracker = ref vmState.AccessTracker;
            if (vm.TxTracer.IsTracingAccess)
            {
                accessTracker.WarmUp(in storageCell);
            }

            if (witnessGasCharged)
            {
                accessTracker.WarmUp(in storageCell);
            }
            // If the storage cell is still cold, apply the higher cold access cost and mark it as warm.
            else if (accessTracker.IsCold(in storageCell))
            {
                // after eip4762 is enabled, we don't charge cold cost as it is replaced by stateless access costs
                result = spec.IsEip4762Enabled || UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                accessTracker.WarmUp(in storageCell);
            }
            // For SLOAD operations on already warmed-up storage, apply a lower warm-read cost.
            else if (spec.IsEip4762Enabled || storageAccessType == StorageAccessType.SLOAD)
            {
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates and deducts the gas cost for accessing a specific memory region.
    /// </summary>
    /// <param name="vmState">The current EVM state.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        // Calculate additional gas cost for any memory expansion.
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <returns><c>true</c> if there was sufficient gas; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="refund">The gas amount to refund.</param>
    /// <param name="gasAvailable">The current gas available.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }
}
