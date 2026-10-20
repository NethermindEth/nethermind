// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Intrinsics;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    /// <summary>How the compiler runs an opcode.</summary>
    internal enum IlKind : byte
    {
        /// <summary>Handed back to the interpreter at the instruction.</summary>
        Unsupported,
        Push,
        Dup,
        Swap,
        Pop,
        JumpDest,
        Jump,
        JumpIf,
        ProgramCounter,
        Gas,
        Stop,
        /// <summary>A body whose gas and stack checks the dispatch performs; the compiler hoists them into the segment.</summary>
        CheckedBody,
        /// <summary>A checked opcode the compiler runs itself, through its operation on the top stack slot.</summary>
        Native,
        /// <summary>MLOAD: a word already in memory read in place, anything else through its body.</summary>
        MLoad,
        /// <summary>MSTORE: a word that needs no clearing and no new backing written in place, anything else through its body.</summary>
        MStore,
        /// <summary>A memory opcode run through its body inside the segment, at the gas and stack the interpreter would hold.</summary>
        EmbeddedBody,
        /// <summary>A body that charges and checks for itself and may halt or suspend the frame.</summary>
        Body,
        /// <summary>A body after which the frame always ends.</summary>
        TerminatingBody,
    }

    internal sealed class IlOp
    {
        public IlOp Clone() => (IlOp)MemberwiseClone();

        public IlKind Kind;
        public ulong StaticGas;
        public int StackInputs;
        public int StackGrowth;
        public int Count;
        /// <summary>For a swap, the position from the top of the upper of its two slots: 1 but for EXCHANGE; the lower is <see cref="Count"/> below it.</summary>
        public int Upper = 1;
        /// <summary>For DUPN, SWAPN and EXCHANGE, whose depths each instruction's immediate gives (<see cref="IlEip8024Op"/>): which decoding.</summary>
        public Eip8024Immediate Immediate;
        public bool UsesVm;
        /// <summary>Whether the opcode charges gas beyond <see cref="StaticGas"/>, which a segment cannot precompute.</summary>
        public bool Dynamic;
        /// <summary>Whether the order of the opcode's own gas and stack checks is not known, so a failed segment precondition cannot be walked past it.</summary>
        public bool Opaque;
        public bool IsCallDataLoad;
        public MethodInfo? Execute;
        /// <summary>The body's <c>Execute</c> entry point, called through <c>calli</c> so the JIT never inlines it.</summary>
        public nint ExecutePointer;
        /// <summary>The helper a <see cref="IlKind.Native"/> opcode runs on the top slot.</summary>
        public MethodInfo? Native;
        /// <summary>The helper a <see cref="IlKind.Native"/> binary opcode runs when one operand is a constant.</summary>
        public MethodInfo? NativeConst;
        /// <summary>For a comparison, the helper that answers it as a branchable flag instead of a word.</summary>
        public MethodInfo? Predicate;
        public MethodInfo? PredicateConst;
        /// <summary>The opcode's <c>Operation</c>, run at compile time to fold constant operands; for the shifts, whose operations are internal to the core, the plugin's own twin of it.</summary>
        public MethodInfo? Operation;
        /// <summary>The operation type the body runs, one of its generic arguments: what the emitter tells operations apart by.</summary>
        public Type? OperationType;
        public IlFamily Family;
        public bool IsPredicate;
        public Type? BodyType;
        public string? Name;
    }

    /// <summary>The operation shapes of <see cref="IlKind.Native"/> opcodes.</summary>
    internal enum IlFamily : byte
    {
        None,
        /// <summary><see cref="EvmInstructions.IOpMath2Param"/>: two words to one, by reference.</summary>
        Math2,
        /// <summary><see cref="EvmInstructions.IOpBitwise"/>: two words to one, by value.</summary>
        Bitwise,
        /// <summary><see cref="EvmInstructions.IOpMath1Param"/>: one word to one.</summary>
        Math1,
        /// <summary><see cref="EvmInstructions.IOpShift"/>: an amount and a word to one.</summary>
        Shift,
    }


    internal enum Eip8024Immediate : byte { None, Single, Pair }

    /// <summary>
    /// The op of a DUPN, SWAPN or EXCHANGE for <paramref name="immediate"/>, decoded as the core does (EIP-8024); null for an
    /// immediate the core rejects, where the interpreter ends the frame with <c>BadInstruction</c>.
    /// </summary>
    internal static IlOp? IlEip8024Op(IlOp op, byte immediate)
    {
        IlOp resolved = op.Clone();
        if (op.Immediate == Eip8024Immediate.Single)
        {
            if ((uint)(immediate - 0x5B) <= 0x24)
                return null;
            resolved.Count = (immediate + 145) & 0xFF;
            resolved.StackInputs = op.Kind == IlKind.Dup ? resolved.Count : resolved.Count + 1;
            return resolved;
        }

        if ((uint)(immediate - 0x52) <= 0x2D)
            return null;
        int k = immediate ^ 0x8F, q = k >> 4, r = k & 0x0F;
        (int n, int m) = q < r ? (q + 2, r + 2) : (r + 2, 30 - q);
        resolved.Upper = Math.Min(n, m);
        resolved.Count = Math.Abs(m - n);
        resolved.StackInputs = Math.Max(n, m);
        return resolved;
    }

    /// <summary>How the compiler runs each opcode of a fork, from what the core reports of its bodies; names are recognized only to pick a faster path.</summary>
    private static IlOp[] IlDescribeOps(OpcodeBodySet bodies)
    {
        IlOp[] ops = new IlOp[256];
        for (int opcode = 0; opcode < 256; opcode++)
        {
            IlOp op = new() { Kind = IlKind.Unsupported };
            ops[opcode] = op;
            OpcodeBody body = bodies.Bodies[opcode];
            if (body.Flow == OpcodeFlow.Unrecorded)
            {
                op.Name = "unrecorded";
                continue;
            }

            Instruction instruction = (Instruction)opcode;
            if (body.Flow == OpcodeFlow.ConditionalJump)
            {
                op.Kind = IlKind.JumpIf;
                op.StaticGas = body.FixedGas;
                op.StackInputs = body.StackInputs;
                op.StackGrowth = body.StackGrowth;
                op.Name = "JUMPI";
                continue;
            }

            Type bodyType = body.BodyType!;
            op.Name = bodyType.Name;
            op.BodyType = bodyType;
            // The Execute method, found best effort, only lets the JIT inline a checked body and the warm-up compile it; without it the body runs through the pointer.
            op.ExecutePointer = body.Execute;
            op.Execute = BodyExecuteMethod(bodyType);
            bool fixedGas = body.HasCheckedBody && body.FixedGas != ulong.MaxValue;

            bool IsBody(string name) => bodyType.Name == name || bodyType.Name.StartsWith(name + "`", StringComparison.Ordinal);

            if (!body.IsDefined)
            {
                op.Kind = IlKind.TerminatingBody;
                continue;
            }

            switch (instruction)
            {
                case Instruction.STOP when IsBody("StopOpcode"):
                    op.Kind = IlKind.Stop;
                    continue;
                case Instruction.PUSH0 when IsBody("Push0Opcode") && fixedGas:
                    op.Kind = IlKind.Push;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
                case Instruction.PUSH2 when IsBody("Push2Opcode") && !bodies.UsesCallDestinations:
                    op.Kind = IlKind.Push;
                    op.StaticGas = VeryLowGasCost.GasCost;
                    op.StackGrowth = 1;
                    op.Count = 2;
                    continue;
                case >= Instruction.PUSH1 and <= Instruction.PUSH32 when IsBody("PushOpcode") && fixedGas:
                    op.Kind = IlKind.Push;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    op.Count = opcode - (int)Instruction.PUSH1 + 1;
                    continue;
                case >= Instruction.DUP1 and <= Instruction.DUP16 when IsBody("DupOpcode") && fixedGas:
                    op.Kind = IlKind.Dup;
                    op.StaticGas = body.FixedGas;
                    op.Count = opcode - (int)Instruction.DUP1 + 1;
                    op.StackInputs = op.Count;
                    op.StackGrowth = 1;
                    continue;
                case >= Instruction.SWAP1 and <= Instruction.SWAP16 when IsBody("SwapOpcode") && fixedGas:
                    op.Kind = IlKind.Swap;
                    op.StaticGas = body.FixedGas;
                    op.Count = opcode - (int)Instruction.SWAP1 + 1;
                    op.StackInputs = op.Count + 1;
                    continue;
                // EIP-8024's bodies charge VeryLow before they decode their immediate, and report no fixed gas.
                case Instruction.DUPN when IsBody("DupNOpcode"):
                    op.Kind = IlKind.Dup;
                    op.StaticGas = VeryLowGasCost.GasCost;
                    op.StackGrowth = 1;
                    op.Immediate = Eip8024Immediate.Single;
                    continue;
                case Instruction.SWAPN when IsBody("SwapNOpcode"):
                    op.Kind = IlKind.Swap;
                    op.StaticGas = VeryLowGasCost.GasCost;
                    op.Immediate = Eip8024Immediate.Single;
                    continue;
                case Instruction.EXCHANGE when IsBody("ExchangeOpcode"):
                    op.Kind = IlKind.Swap;
                    op.StaticGas = VeryLowGasCost.GasCost;
                    op.Immediate = Eip8024Immediate.Pair;
                    continue;
                case Instruction.POP when IsBody("PopOpcode") && fixedGas:
                    op.Kind = IlKind.Pop;
                    op.StaticGas = body.FixedGas;
                    op.StackInputs = 1;
                    op.StackGrowth = -1;
                    continue;
                case Instruction.JUMPDEST when IsBody("JumpDestOpcode") && fixedGas:
                    op.Kind = IlKind.JumpDest;
                    op.StaticGas = body.FixedGas;
                    continue;
                case Instruction.JUMP when IsBody("JumpOpcode"):
                    op.Kind = IlKind.Jump;
                    op.StaticGas = JumpGasCost.GasCost;
                    op.StackInputs = 1;
                    op.StackGrowth = -1;
                    continue;
                case Instruction.PC when IsBody("ProgramCounterOpcode") && fixedGas:
                    op.Kind = IlKind.ProgramCounter;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
                case Instruction.GAS when IsBody("GasOpcode") && fixedGas:
                    op.Kind = IlKind.Gas;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
            }

            if (op.ExecutePointer == 0 || IsBody("DupNOpcode") || IsBody("SwapNOpcode") || IsBody("ExchangeOpcode") || body.MayJump)
            {
                op.Name += " (unsupported)";
                continue;
            }

            if (IlTryNative(body, bodyType, op))
                continue;

            if (IsBody("MLoadOpcode") || IsBody("MStoreOpcode") || IsBody("MStore8Opcode"))
            {
                // Memory words: VeryLow gas and the stack checked before expansion, as the bodies do.
                op.Kind = IsBody("MLoadOpcode") ? IlKind.MLoad : IsBody("MStoreOpcode") ? IlKind.MStore : IlKind.EmbeddedBody;
                op.StaticGas = VeryLowGasCost.GasCost;
                op.StackInputs = op.Kind == IlKind.MLoad ? 1 : 2;
                op.StackGrowth = op.Kind == IlKind.MLoad ? 0 : -2;
                op.Dynamic = true;
                continue;
            }

            if (fixedGas)
            {
                op.IsCallDataLoad = IsBody("CallDataLoadOpcode");
                op.Kind = IlKind.CheckedBody;
                op.StaticGas = body.FixedGas;
                op.StackInputs = body.StackInputs;
                op.StackGrowth = body.StackGrowth;
                op.UsesVm = body.UsesVm;
                continue;
            }

            op.Kind = body.Flow == OpcodeFlow.Terminates ? IlKind.TerminatingBody : IlKind.Body;
            if (op.Kind == IlKind.Body && !IsBody("CallOpcode") && !IsBody("CreateOpcode") && IlEmbeddableStackEffect(instruction) is var (inputs, outputs))
            {
                // Charges and checks in an order the compiler does not model: inside a segment it runs at the interpreter's exact gas and stack, and a segment failing before it hands back whole.
                op.Kind = IlKind.EmbeddedBody;
                op.StackInputs = inputs;
                op.StackGrowth = outputs - inputs;
                op.Dynamic = true;
                op.Opaque = true;
            }
        }

        return ops;
    }

    /// <summary>The public static <c>Execute</c> behind <see cref="OpcodeBody.Execute"/>, or null, for inlinable direct calls and the warm-up; best effort, as the body types are not public.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    private static MethodInfo? BodyExecuteMethod(Type bodyType)
    {
        MethodInfo? execute = bodyType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static, BodyParameters);
        return execute?.ReturnType == typeof(EvmExceptionType) ? execute : null;
    }

    private static readonly Type[] BodyParameters =
        [typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(VirtualMachine<EthereumGasPolicy>), typeof(nint).MakeByRefType()];

    /// <summary>Describes a checked arithmetic, comparison, bitwise or shift body as an operation the compiler runs itself: the opcode's own <c>Operation</c>, on the top stack slots.</summary>
    private static bool IlTryNative(OpcodeBody body, Type bodyType, IlOp op)
    {
        if (!body.HasCheckedBody || body.FixedGas == ulong.MaxValue || !bodyType.IsGenericType)
            return false;

        string name = bodyType.Name;
        Type[] arguments = bodyType.GetGenericArguments();
        Type? Find(Type contract) => Array.Find(arguments, contract.IsAssignableFrom);
        // The shift operations are internal to the core, so they are found by name; they run as direct forms or bodies.
        Type? FindShift() => Array.Find(arguments, static a => a.Name is "OpShl" or "OpShr");

        string? helper = null;
        Type? operation = null;
        if (name.StartsWith("Math2Opcode`", StringComparison.Ordinal) && (operation = Find(typeof(EvmInstructions.IOpMath2Param))) is not null)
            // ADD and SUB are alias-safe, as the interpreter's vector path relies on; MUL, DIV and MOD are not.
            helper = operation == typeof(EvmInstructions.OpAdd) || operation == typeof(EvmInstructions.OpSub) ? nameof(IlMath2InPlace) : nameof(IlMath2);
        else if (name.StartsWith("BitwiseOpcode`", StringComparison.Ordinal) && (operation = Find(typeof(EvmInstructions.IOpBitwise))) is not null)
            helper = nameof(IlBitwise);
        else if (name.StartsWith("Math1Opcode`", StringComparison.Ordinal) && Vector256.IsHardwareAccelerated && (operation = Find(typeof(EvmInstructions.IOpMath1Param))) is not null)
            // ISZERO's operation is only used where a 256-bit register exists.
            helper = nameof(IlMath1);
        else if (name.StartsWith("ShiftOpcode`", StringComparison.Ordinal) && (operation = FindShift()) is not null)
            helper = "Shift";
        else if (name.StartsWith("CountLeadingZerosOpcode`", StringComparison.Ordinal) && Vector256.IsHardwareAccelerated)
            (operation, helper) = (typeof(IlOpClz), nameof(IlMath1));

        if (helper is null)
            return false;

        op.Kind = IlKind.Native;
        op.StaticGas = body.FixedGas;
        op.StackInputs = body.StackInputs;
        op.StackGrowth = body.StackGrowth;
        op.UsesVm = body.UsesVm;
        op.Native = helper == "Shift" ? null : IlHelper(helper, operation!);
        op.Family = helper switch
        {
            nameof(IlBitwise) => IlFamily.Bitwise,
            nameof(IlMath1) => IlFamily.Math1,
            "Shift" => IlFamily.Shift,
            _ => IlFamily.Math2,
        };
        op.OperationType = operation;
        op.Operation = helper == "Shift"
            ? typeof(IlEvm).GetMethod(operation!.Name == "OpShl" ? nameof(IlFoldShl) : nameof(IlFoldShr), BindingFlags.NonPublic | BindingFlags.Static)
            : operation!.GetMethod("Operation", BindingFlags.Public | BindingFlags.Static);
        op.IsPredicate = operation == typeof(EvmInstructions.OpLt) || operation == typeof(EvmInstructions.OpGt) ||
            operation == typeof(EvmInstructions.OpSLt) || operation == typeof(EvmInstructions.OpSGt) ||
            operation == typeof(EvmInstructions.OpBitwiseEq) || operation == typeof(EvmInstructions.OpIsZero);
        op.NativeConst = op.Family switch
        {
            IlFamily.Math2 => IlHelper(nameof(IlMath2Const), operation),
            IlFamily.Bitwise => IlHelper(nameof(IlBitwiseConst), operation),
            _ => null,
        };
        if (op.IsPredicate)
        {
            op.Predicate = op.Family switch
            {
                IlFamily.Math2 => IlHelper(nameof(IlPredicate2), operation),
                IlFamily.Bitwise => IlHelper(nameof(IlPredicateBitwise), operation),
                _ => IlHelper(nameof(IlPredicate1), operation),
            };
            op.PredicateConst = op.Family switch
            {
                IlFamily.Math2 => IlHelper(nameof(IlPredicate2Const), operation),
                IlFamily.Bitwise => IlHelper(nameof(IlPredicateBitwiseConst), operation),
                _ => null,
            };
        }

        return op.Operation is not null;
    }

    /// <summary>SHL's operation, for folding at compile time: the core's is internal. A shift of 256 or more is zero.</summary>
    private static void IlFoldShl(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 256)
            result = default;
        else
            b.LeftShift((int)a.u0, out result);
    }

    private static void IlFoldShr(in UInt256 a, in UInt256 b, out UInt256 result)
    {
        if (!a.IsUint64 || a.u0 >= 256)
            result = default;
        else
            b.RightShift((int)a.u0, out result);
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    private static MethodInfo IlHelper(string name, Type operation) =>
        typeof(IlEvm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(operation);
}
