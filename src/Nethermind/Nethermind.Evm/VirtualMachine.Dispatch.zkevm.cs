// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using InlineIL;
using Nethermind.Core;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    internal ref struct DispatchState
    {
        /// <summary>The frame's gas.</summary>
        /// <remarks>
        /// Its remaining execution gas is stale while the chain runs: the chain carries that value in the
        /// dispatch signature and writes it back before any body that takes this policy, and as it leaves.
        /// </remarks>
        public ref TGasPolicy Gas;

        /// <summary>The table the chain dispatches through, for handlers that reload it after their body.</summary>
        public nint* OpcodeHandlers;

        public VirtualMachine<TGasPolicy> Vm;

        /// <summary>Where the chain stopped. Written only as the chain leaves.</summary>
        public nint FinalProgramCounter;

        /// <summary>The stack head the chain stopped at. Written only as the chain leaves.</summary>
        /// <remarks>
        /// Every handler's <c>head</c> argument carries the head, and the frame's <see cref="EvmStack.Head"/> is stale
        /// while the chain runs: any handler in the table must take the head from the argument, store it before code
        /// that reads <see cref="EvmStack.Head"/>, and reload it after.
        /// </remarks>
        public nint Head;
    }

    // The shared table code declares its entries with the host's dispatch signature. The guest's handlers take the
    // wider one of RawCalliHelper below, so these factories cast them in and every guest call site casts them back.
    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        OpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        AsTableEntry(&RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OnFlag>);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        TerminatingOpcodeHandler<TOpcode, TTracingInst, TCancelable>()
        where TOpcode : struct, IOpcodeBody
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        AsTableEntry(&RawCalliHelper.ExecuteOpcode<TOpcode, TTracingInst, TCancelable, OffFlag>);

    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>
        JumpIfOpcodeHandler<TTracingInst, TCancelable>()
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag =>
        AsTableEntry(&RawCalliHelper.ExecuteJumpIfOpcode<TTracingInst, TCancelable>);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType> AsTableEntry(
        delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType> handler) =>
        (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>)handler;

    /// <summary>Runs the current frame's bytecode until it halts, faults, or yields a child frame.</summary>
    /// <param name="programCounter">On entry the offset to resume from; on exit the offset reached.</param>
    /// <returns>
    /// The halting reason; <c>None</c>, <c>Stop</c> and <c>Revert</c> are normal halts, while <c>Suspend</c>
    /// indicates a yielded child frame.
    /// </returns>
    /// <remarks>
    /// The guest cannot be cancelled (<see cref="DispatchFlags.ConstCancelable"/>), so a cancelable table runs the
    /// chain to its end like any other.
    /// </remarks>
    [SkipLocalsInit]
    private EvmExceptionType RunDispatchLoop<TTracingInst, TCancelable>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        if ((nuint)programCounter >= (nuint)stack.CodeLength)
            return EvmExceptionType.None;

        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>[] handlers = _opcodeHandlers;

        // Safety: the 256-entry opcode table remains pinned for the complete tail-call chain. Every bytecode read
        // lands in the code or in the padding that follows it (DispatchFlags.PaddedCode), and a byte is a valid
        // table index.
        fixed (delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, int, EvmExceptionType>* opcodeHandlers = &handlers[0])
        {
            nint* table = (nint*)opcodeHandlers;
            // Unscoped because a function pointer cannot declare its parameters scoped; the chain ends before this call does.
            DispatchState state = new() { Gas = ref Unsafe.AsRef(in gas), OpcodeHandlers = table, Vm = this };

            byte opcode = Unsafe.Add(ref stack.Code, programCounter);
            EvmExceptionType exceptionType =
                ((delegate*<ref EvmStack, ulong, ref DispatchState, nint, nint, nint*, ref byte, nint, EvmExceptionType>)table[opcode])(
                    ref stack, TGasPolicy.GetRemainingGas(in gas), ref state, programCounter, stack.Head, table, ref stack.Code, stack.CodeLength);
            stack.Head = state.Head;
            programCounter = state.FinalProgramCounter;
            return exceptionType;
        }
    }

    /// <summary>The guest's dispatch handlers, each of which ends in a tail call through the opcode table.</summary>
    /// <remarks>
    /// See the host's <c>RawCalliHelper</c> in <c>VirtualMachine.Dispatch.cs</c> for the name. The handlers take eight
    /// arguments, all of which RV64 passes in registers: the remaining execution gas, the stack head, the table, the
    /// bytecode and its length ride from handler to handler, so a handler whose body stays inline neither loads nor
    /// stores them. x64 passes only four (Windows) or six (SysV) in registers, which is why the host keeps five.
    /// <para>
    /// A checked body pays its fixed cost from the carried gas and runs on a copy of the stack that holds the carried
    /// head and bytecode, and dispatch moves the head by the body's declared growth, so neither reaches memory on the
    /// way. The frame's gas policy and head are current only around bodies that take them, and once the chain leaves.
    /// Every code is padded (<see cref="DispatchFlags.PaddedCode"/>), so no handler tests the program counter against
    /// the code length, and the guest neither counts opcodes nor polls for cancellation.
    /// </para>
    /// </remarks>
    private static partial class RawCalliHelper
    {
        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteOpcode<TOpcode, TTracingInst, TCancelable, TContinuable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TOpcode : struct, IOpcodeBody
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
            where TContinuable : struct, IFlag
        {
            // Only a traced run reads the opcode out of the bytecode, and the tracer reads the head from memory.
            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
            {
                stack.Head = head;
                Instruction instruction = (Instruction)Unsafe.Add(ref code, pc);
                state.Vm.StartInstructionTrace(instruction, gas, (int)pc, in stack);
            }

            pc++;
            EvmExceptionType exceptionType;
            if (TOpcode.HasCheckedBody)
            {
                // A policy holding only the remaining gas sees the same fixed charge, and a local one stays in registers.
                Unsafe.SkipInit(out TGasPolicy fixedGas);
                TGasPolicy.SetRemainingGas(ref fixedGas, gas);
                if (!TOpcode.TryConsumeGas(ref fixedGas))
                    return ExitChain(ref state, TGasPolicy.GetRemainingGas(in fixedGas), pc, head, EvmExceptionType.OutOfGas);
                gas = TGasPolicy.GetRemainingGas(in fixedGas);

                if (TOpcode.StackInputs != 0 && TOpcode.StackGrowth > 0)
                {
                    // One unsigned test bounds the depth on both sides: below the inputs the difference wraps past the limit.
                    nint aboveInputs = head - TOpcode.StackInputs;
                    if ((nuint)aboveInputs >= (nuint)(EvmStack.MaxStackSize - TOpcode.StackGrowth - TOpcode.StackInputs))
                        return ExitChain(ref state, gas, pc, head,
                            aboveInputs < 0 ? EvmExceptionType.StackUnderflow : EvmExceptionType.StackOverflow);
                }
                else
                {
                    if (TOpcode.StackInputs != 0 && head < TOpcode.StackInputs)
                        return ExitChain(ref state, gas, pc, head, EvmExceptionType.StackUnderflow);
                    if (TOpcode.StackGrowth > 0 && head >= EvmStack.MaxStackSize - TOpcode.StackGrowth)
                        return ExitChain(ref state, gas, pc, head, EvmExceptionType.StackOverflow);
                }

                EvmExceptionType checkedResult = EvmExceptionType.None;
                if (TOpcode.MovesHeadOnly)
                {
                    head += TOpcode.StackGrowth;
                }
                else if (!TOpcode.CallsOutOfLine)
                {
                    EvmStack local = new(in stack, head, ref code);
                    checkedResult = TOpcode.Execute(ref local, ref fixedGas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                    head += TOpcode.StackGrowth;
                    Debug.Assert(local.Head == head, "StackGrowth must be the net change the checked body makes.");
                }
                else
                {
                    stack.Head = head;
                    checkedResult = TOpcode.Execute(ref stack, ref fixedGas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                    head = stack.Head;
                }
                Debug.Assert(checkedResult == EvmExceptionType.None, "HasCheckedBody must not fail after dispatch validates its preconditions.");
                exceptionType = EvmExceptionType.None;
            }
            else if (TOpcode.ChargesFixedGas)
            {
                // A policy holding only the remaining gas sees the same fixed charges, and a local one can stay in registers.
                Unsafe.SkipInit(out TGasPolicy fixedGas);
                TGasPolicy.SetRemainingGas(ref fixedGas, gas);
                stack.Head = head;
                exceptionType = TOpcode.Execute(ref stack, ref fixedGas, TOpcode.UsesVm ? state.Vm : null!, ref pc);
                head = stack.Head;
                gas = TGasPolicy.GetRemainingGas(in fixedGas);
            }
            else
            {
                TGasPolicy.SetRemainingGas(ref state.Gas, gas);
                stack.Head = head;
                exceptionType = TOpcode.Execute(ref stack, ref state.Gas, state.Vm, ref pc);
                head = stack.Head;
                gas = TGasPolicy.GetRemainingGas(in state.Gas);
            }

            if ((!TOpcode.HasCheckedBody && !TOpcode.StaysInline) || TOpcode.CallsOutOfLine)
            {
                // Reloaded, not held: live across an out-of-line call they would each take a callee-saved register.
                handlers = state.OpcodeHandlers;
                code = ref stack.Code;
                codeLength = stack.CodeLength;
            }

            if (!TContinuable.IsActive)
                goto Exit;

            if (!TOpcode.HasCheckedBody && exceptionType != EvmExceptionType.None)
                goto Exit;

            // Padded code reads STOP past its end, so wherever a successful opcode leaves the counter it resolves to a
            // handler. A failed one can leave it anywhere, so this read waits for the status.
            nint next = handlers[Unsafe.Add(ref code, pc)];

            Debug.Assert(state.Vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                state.Vm.EndInstructionTrace(gas);

            // Keep the target in a real local so InlineIL can place it above the outgoing arguments.
            IL.EnsureLocal(in next);

            IL.Emit.Ldarg(nameof(stack));
            IL.Emit.Ldarg(nameof(gas));
            IL.Emit.Ldarg(nameof(state));
            IL.Emit.Ldarg(nameof(pc));
            IL.Emit.Ldarg(nameof(head));
            IL.Emit.Ldarg(nameof(handlers));
            IL.Emit.Ldarg(nameof(code));
            IL.Emit.Ldarg(nameof(codeLength));
            IL.Push(next);
            IL.Emit.Tail();
            IL.Emit.Calli(new StandAloneMethodSig(
                CallingConventions.Standard,
                TypeRef.Type<EvmExceptionType>(),
                TypeRef.Type<EvmStack>().MakeByRefType(),
                TypeRef.Type<ulong>(),
                TypeRef.Type<DispatchState>().MakeByRefType(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>(),
                TypeRef.Type<nint>().MakePointerType(),
                TypeRef.Type<byte>().MakeByRefType(),
                TypeRef.Type<nint>()));
            IL.Emit.Ret();
            throw IL.Unreachable();

        Exit:
            return ExitChain(ref state, gas, pc, head, exceptionType);
        }

        /// <summary>Writes back what the chain carries, and leaves it with <paramref name="exceptionType"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static EvmExceptionType ExitChain(ref DispatchState state, ulong gas, nint pc, nint head, EvmExceptionType exceptionType)
        {
            TGasPolicy.SetRemainingGas(ref state.Gas, gas);
            state.Head = head;
            state.FinalProgramCounter = pc;
            return exceptionType;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static EvmExceptionType ExecuteJumpIfOpcode<TTracingInst, TCancelable>(
            ref EvmStack stack,
            ulong gas,
            ref DispatchState state,
            nint pc,
            nint head,
            nint* handlers,
            ref byte code,
            nint codeLength)
            where TTracingInst : struct, IFlag
            where TCancelable : struct, IFlag
        {
            VirtualMachine<TGasPolicy> vm = state.Vm;

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
            {
                stack.Head = head;
                Instruction instruction = (Instruction)Unsafe.Add(ref code, pc);
                vm.StartInstructionTrace(instruction, gas, (int)pc, in stack);
            }

            pc++;
            nint fallthroughPc = pc;
            // Every JUMPI charge is fixed, as for a ChargesFixedGas body.
            Unsafe.SkipInit(out TGasPolicy fixedGas);
            TGasPolicy.SetRemainingGas(ref fixedGas, gas);
            stack.Head = head;
            OpcodeResult result = TTracingInst.IsActive
                ? EvmInstructions.InstructionJumpIf(ref stack, ref fixedGas, vm, pc)
                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(ref stack, ref fixedGas, vm, pc);
            head = stack.Head;
            gas = TGasPolicy.GetRemainingGas(in fixedGas);
            pc = result.ProgramCounter;
            handlers = state.OpcodeHandlers;
            code = ref stack.Code;
            codeLength = stack.CodeLength;

            if (result.Exception != EvmExceptionType.None)
                return ExitChain(ref state, gas, pc, head, result.Exception);

            Debug.Assert(vm.ReturnData is null,
                "A handler that stages ReturnData must report a non-None status, or dispatch will continue past the halt");

            if (TTracingInst.IsActive && typeof(TTracingInst) != typeof(SilentInstructionFlag))
                vm.EndInstructionTrace(gas);

            // Each outcome resolves its own successor and transfers from its own site, so the predictor gets
            // a taken entry and a fall-through entry to learn separately. Sharing one lookup would let the
            // JIT fold the two transfers back into a single indirect branch.
            if (pc != fallthroughPc)
            {
                nint taken = handlers[Unsafe.Add(ref code, pc)];
                IL.EnsureLocal(in taken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(head));
                IL.Emit.Ldarg(nameof(handlers));
                IL.Emit.Ldarg(nameof(code));
                IL.Emit.Ldarg(nameof(codeLength));
                IL.Push(taken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>().MakePointerType(),
                    TypeRef.Type<byte>().MakeByRefType(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }
            else
            {
                nint notTaken = handlers[Unsafe.Add(ref code, fallthroughPc)];
                IL.EnsureLocal(in notTaken);

                IL.Emit.Ldarg(nameof(stack));
                IL.Emit.Ldarg(nameof(gas));
                IL.Emit.Ldarg(nameof(state));
                IL.Emit.Ldarg(nameof(pc));
                IL.Emit.Ldarg(nameof(head));
                IL.Emit.Ldarg(nameof(handlers));
                IL.Emit.Ldarg(nameof(code));
                IL.Emit.Ldarg(nameof(codeLength));
                IL.Push(notTaken);
                IL.Emit.Tail();
                IL.Emit.Calli(new StandAloneMethodSig(
                    CallingConventions.Standard,
                    TypeRef.Type<EvmExceptionType>(),
                    TypeRef.Type<EvmStack>().MakeByRefType(),
                    TypeRef.Type<ulong>(),
                    TypeRef.Type<DispatchState>().MakeByRefType(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>(),
                    TypeRef.Type<nint>().MakePointerType(),
                    TypeRef.Type<byte>().MakeByRefType(),
                    TypeRef.Type<nint>()));
                IL.Emit.Ret();
            }

            throw IL.Unreachable();
        }
    }
}
