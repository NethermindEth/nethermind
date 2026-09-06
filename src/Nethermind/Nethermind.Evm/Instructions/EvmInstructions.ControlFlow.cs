// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Pushes the current program counter (minus one) onto the EVM stack.
    /// This is used to obtain the current execution point within the code.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the program counter is pushed.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionProgramCounter<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the base gas cost for reading the program counter.
        if (!TGasPolicy.UpdateGas<BaseGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        // The program counter pushed is adjusted by -1 to reflect the correct opcode location.
        return stack.PushUInt32<TTracingInst, OnFlag>((uint)(programCounter - 1));
    }

    /// <summary>
    /// Marks a valid jump destination.
    /// This instruction only deducts the jump destination gas cost without modifying the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpDest<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Deduct the gas cost specific for a jump destination marker.
        if (!TGasPolicy.UpdateGas<JumpDestGasCost>(ref gas)) return EvmExceptionType.OutOfGas;

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an unconditional jump.
    /// Pops a jump destination from the stack and validates it.
    /// If the destination is valid, updates the program counter; otherwise, returns an error.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the jump destination is popped.</param>
    /// <param name="gas">Reference to the gas state; reduced by the gas cost for jumping.</param>
    /// <param name="programCounter">The program counter; the destination is returned rather than written back.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; <see cref="EvmExceptionType.StackUnderflow"/> or <see cref="EvmExceptionType.InvalidJumpDestination"/>
    /// on failure.
    /// </returns>
    [SkipLocalsInit]
    public static OpcodeResult InstructionJump<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => InstructionJump<TGasPolicy, OffFlag>(ref stack, ref gas, vm, programCounter);

    /// <summary>
    /// <see cref="InstructionJump{TGasPolicy}"/> for non-traced tables: a valid taken jump also
    /// counts and charges the target <c>JUMPDEST</c> and leaves PC on the instruction after it,
    /// eliminating the marker's dispatch.
    /// </summary>
    [SkipLocalsInit]
    internal static OpcodeResult InstructionJumpAndSkipJumpDest<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => InstructionJump<TGasPolicy, OnFlag>(ref stack, ref gas, vm, programCounter);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OpcodeResult InstructionJump<TGasPolicy, TSkipJumpDest>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TSkipJumpDest : struct, IFlag
    {
        // Deduct the gas cost for performing a jump.
        if (!TGasPolicy.UpdateGas<JumpGasCost>(ref gas)) return new OpcodeResult(programCounter, EvmExceptionType.OutOfGas);
        // Pop the jump destination from the stack.
        if (!stack.EnsureDepth(1)) goto StackUnderflow;
        // Validate the jump destination and update the program counter if valid.
        nint destination = JumpDestination(ref stack.PopBytesByRefUnchecked(), vm.VmState.Env);
        if (destination < 0) goto InvalidJumpDestination;
        if (!SkipJumpDest<TGasPolicy, TSkipJumpDest>(vm, ref gas, destination, out programCounter))
            return new OpcodeResult(programCounter, EvmExceptionType.OutOfGas);
        // Prefetch the cache line at the jump destination since hardware prefetcher can't predict jumps.
        PrefetchCodeAtDestination(ref stack, programCounter);

        return new OpcodeResult(programCounter, EvmExceptionType.None);
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new OpcodeResult(programCounter, EvmExceptionType.StackUnderflow);
    InvalidJumpDestination:
        return new OpcodeResult(programCounter, EvmExceptionType.InvalidJumpDestination);
    }

    /// <summary>
    /// Executes a conditional jump.
    /// Pops a jump destination and a condition from the stack. If the condition is non-zero,
    /// attempts to jump to the specified destination.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the jump destination and condition are popped.</param>
    /// <param name="gas">Reference to the gas state; reduced by the cost for conditional jump.</param>
    /// <param name="programCounter">The program counter; the destination is returned rather than written back.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; returns <see cref="EvmExceptionType.StackUnderflow"/>
    /// or <see cref="EvmExceptionType.InvalidJumpDestination"/> on error.
    /// </returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static OpcodeResult InstructionJumpIf<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => InstructionJumpIf<TGasPolicy, OffFlag>(ref stack, ref gas, vm, programCounter);

    /// <summary>
    /// <see cref="InstructionJumpIf{TGasPolicy}"/> for non-traced tables: a valid taken jump also
    /// counts and charges the target <c>JUMPDEST</c> and leaves PC on the instruction after it,
    /// eliminating the marker's dispatch. Untaken jumps are unchanged.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static OpcodeResult InstructionJumpIfAndSkipJumpDest<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => InstructionJumpIf<TGasPolicy, OnFlag>(ref stack, ref gas, vm, programCounter);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OpcodeResult InstructionJumpIf<TGasPolicy, TSkipJumpDest>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm, nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TSkipJumpDest : struct, IFlag
    {
        // Deduct the high gas cost for a conditional jump.
        if (!TGasPolicy.UpdateGas<JumpIGasCost>(ref gas)) return new OpcodeResult(programCounter, EvmExceptionType.OutOfGas);
        // The condition sits directly below the destination, so one depth check covers both.
        if (!stack.EnsureDepth(2)) goto StackUnderflow;
        ref byte condition = ref stack.Pop2BytesByRefUnchecked();

        // Only a taken jump reads the destination, so an untaken one never decodes it.
        if (!EvmStack.IsSlotZero(ref condition))
        {
            nint destination = JumpDestination(ref Unsafe.Add(ref condition, EvmStack.WordSize), vm.VmState.Env);
            if (destination < 0) goto InvalidJumpDestination;
            if (!SkipJumpDest<TGasPolicy, TSkipJumpDest>(vm, ref gas, destination, out programCounter))
                return new OpcodeResult(programCounter, EvmExceptionType.OutOfGas);
            // Prefetch the cache line at the jump destination since hardware prefetcher can't predict jumps.
            PrefetchCodeAtDestination(ref stack, programCounter);
        }

        return new OpcodeResult(programCounter, EvmExceptionType.None);
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new OpcodeResult(programCounter, EvmExceptionType.StackUnderflow);
    InvalidJumpDestination:
        return new OpcodeResult(programCounter, EvmExceptionType.InvalidJumpDestination);
    }

    /// <summary>Steps past a landed-on <c>JUMPDEST</c> and returns whether its gas charge succeeded.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SkipJumpDest<TGasPolicy, TSkipJumpDest>(VirtualMachine<TGasPolicy> vm, ref TGasPolicy gas, nint destination, out nint programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TSkipJumpDest : struct, IFlag
    {
        programCounter = destination;
        if (TSkipJumpDest.IsActive)
        {
            // Count before charging so an out-of-gas JUMPDEST matches the dispatch loop's ordering.
            vm.OpCodeCount++;
            programCounter++;
            return TGasPolicy.UpdateGas<JumpDestGasCost>(ref gas);
        }

        return true;
    }

    /// <summary>
    /// Stops the execution of the EVM.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionStop<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => EvmExceptionType.Stop;

    /// <summary>
    /// Implements the REVERT opcode.
    /// Pops a memory offset and length from the stack, updates memory gas cost, loads the return data,
    /// and returns a revert exception.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionRevert<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Attempt to pop memory offset and length; if either fails, signal a stack underflow.
        if (!stack.PopMemoryPositionAndUInt256(out UInt256 position, out UInt256 length))
        {
            goto StackUnderflow;
        }

        // Ensure sufficient gas for any required memory expansion.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, in length, ref vm.VmState.Memory) ||
            !vm.VmState.Memory.TryLoad(in position, in length, out ReadOnlyMemory<byte> returnData))
        {
            goto OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();

        return EvmExceptionType.Revert;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the SELFDESTRUCT opcode.
    /// This method handles gas adjustments, account balance transfers,
    /// and marks the executing account for destruction.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfDestruct<TGasPolicy, TEip8037, TEip7708>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag =>
        InstructionSelfDestruct<TGasPolicy, TEip8037, TEip7708, DynamicSelfDestructSpec>(ref stack, ref gas, vm);

    [SkipLocalsInit]
    internal static EvmExceptionType InstructionSelfDestruct<TGasPolicy, TEip8037, TEip7708, TSpec>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
        where TSpec : struct, ISelfDestructSpec
    {
        vm.MetricsCounters.IncrementSelfDestructs();

        VmState<TGasPolicy> vmState = vm.VmState;
        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;

        // SELFDESTRUCT is forbidden during static calls.
        if (vmState.IsStatic)
            goto StaticCallViolation;

        // If Shanghai DDoS protection is active, charge the appropriate gas cost.
        if (TSpec.UseShanghaiDDosProtection(spec))
        {
            if (!TGasPolicy.TryConsumeSelfDestructGas(ref gas))
                goto OutOfGas;
        }

        // Pop the inheritor address from the stack; signal underflow if missing.
        Address inheritor = stack.PopAddress(vm.AddressCache);
        if (inheritor is null)
            goto StackUnderflow;

        // Charge gas for SELFDESTRUCT beneficiary access; if insufficient, signal out-of-gas.
        if (!TSpec.TryConsumeAccountAccessGas<TGasPolicy>(ref gas, spec, in vmState.AccessTracker, vm.TxTracer.IsTracingAccess, inheritor, AccountAccessKind.SelfDestructBeneficiary))
            goto OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
        bool selfdestructOnlyOnSameTx = TSpec.SelfdestructOnlyOnSameTransaction(spec);
        // Mark the executing account for destruction if allowed.
        if (!selfdestructOnlyOnSameTx || createInSameTx)
            vmState.AccessTracker.ToBeDestroyed(executingAccount);

        // Retrieve the current balance for transfer.
        UInt256 result = state.GetBalance(executingAccount);

        if (vm.TxTracer.IsTracingActions)
            vm.TxTracer.ReportSelfDestruct(executingAccount, result, inheritor);

        // Charge gas if transferring to a dead or non-existent account.
        bool inheritorAccountExists = state.AccountExists(inheritor);
        bool chargesNewAccount = TSpec.ClearEmptyAccountWhenTouched(spec) switch
        {
            true => !result.IsZero && state.IsDeadAccount(inheritor),
            false => !inheritorAccountExists && TSpec.UseShanghaiDDosProtection(spec),
        };

        // EIP-8038 adds an ACCOUNT_WRITE execution charge on top of the NEW_ACCOUNT state gas;
        // charge execution first so an execution-gas OOG does not spill state gas.
        bool outOfGas = chargesNewAccount &&
            !((!TSpec.IsEip8038Enabled(spec) || TGasPolicy.UpdateGas(ref gas, Eip8038Constants.AccountWrite))
              && TGasPolicy.TryConsumeNewAccountCreation<TEip8037>(ref gas));

        if (outOfGas) goto OutOfGas;

        // Create or update the inheritor account with the transferred balance.
        if (!inheritorAccountExists)
        {
            state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            state.AddToBalance(inheritor, result, spec);
        }

        // Self-targeting SELFDESTRUCT moves no ETH and emits no log: a pure no-op for the EIP-6780
        // case (not in the destroy list), while EIP-8246 still finalizes but preserves the balance.
        if (inheritor.Equals(executingAccount) && (TSpec.RemoveSelfdestructBurn(spec) || (selfdestructOnlyOnSameTx && !createInSameTx)))
            goto Stop;

        vm.AddSelfDestructLog<TEip8037, TEip7708>(executingAccount, inheritor, result);

        // Subtract the balance from the executing account.
        state.SubtractFromBalance(executingAccount, result, spec);

        // Jump forward to be unpredicted by the branch predictor.
    Stop:
        return EvmExceptionType.Stop;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    /// <summary>
    /// Handles invalid opcodes by deducting a high gas cost and returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionInvalid<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> _)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (!TGasPolicy.UpdateGas<HighGasCost>(ref gas)) return EvmExceptionType.OutOfGas;
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Default handler for undefined opcodes, always returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionBadInstruction<TGasPolicy>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> _)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        => EvmExceptionType.BadInstruction;

    /// <summary>
    /// Reads a jump destination out of the stack slot that holds it, returning it, or <c>-1</c> when it
    /// is not a jump marker.
    /// </summary>
    /// <remarks>
    /// Only the last four bytes of the big-endian word can name a marker; every byte above them just has
    /// to be zero. Testing the slot in place skips the full 256-bit endianness conversion that decoding
    /// it as a <see cref="UInt256"/> would run first. The destination is returned rather than written
    /// through a reference so the caller's counter stays in a register: taking its address pins it to a
    /// stack slot for the whole of the calling instruction.
    /// </remarks>
    /// <param name="slot">The stack slot holding the destination, big-endian.</param>
    /// <param name="env">The current execution environment containing code information.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint JumpDestination(ref byte slot, ExecutionEnvironment env)
    {
        ref ulong parts = ref Unsafe.As<byte, ulong>(ref slot);
        ulong low = Unsafe.Add(ref parts, 3);
        // The low limb's leading four bytes carry the value's high half, so they belong to the zero test.
        if ((parts | Unsafe.Add(ref parts, 1) | Unsafe.Add(ref parts, 2) | (uint)low) != 0)
            return -1;

        // A value above int.MaxValue needs no test of its own: ValidateJump compares unsigned, so the
        // sign-flipped index is far past any code length.
        return JumpDestination((int)BinaryPrimitives.ReverseEndianness((uint)(low >> 32)), env);
    }

    /// <inheritdoc cref="JumpDestination(ref byte, ExecutionEnvironment)"/>
    private static nint JumpDestination(int jumpDestination, ExecutionEnvironment env) =>
        env.CodeInfo.ValidateJump(jumpDestination) ? jumpDestination : -1;

    /// <summary>
    /// Prefetches the cache line at the given program counter location.
    /// Hardware prefetchers cannot predict jump destinations, so we explicitly prefetch
    /// to reduce cache misses after non-sequential control flow.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefetchCodeAtDestination(ref EvmStack stack, nint programCounter)
    {
        if (Sse.IsSupported)
        {
            // Prefetch the cache line containing the jump destination.
            // Also prefetch the next cache line since code often spans multiple lines.
            ref byte code = ref stack.Code;
            nuint dest = (nuint)programCounter;
            nuint codeLength = (nuint)stack.CodeLength;

            if (dest < codeLength)
            {
                unsafe
                {
                    // Best-effort hint: PREFETCHT0 never faults. A GC relocation just
                    // makes the hint useless, not unsafe.
                    Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref code, dest)));
                }
            }
        }
    }
}
