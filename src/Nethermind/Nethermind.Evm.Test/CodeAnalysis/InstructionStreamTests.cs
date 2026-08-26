// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

[TestFixture]
public class InstructionStreamTests
{
    [Test]
    public void TryBuild_StraightLineArithmetic_SumsTheBlockGasOnce()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.PUSH1, 0x02,
            (byte)Instruction.ADD,
            (byte)Instruction.STOP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        Assert.That(stream, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.BlockGas, Has.Length.EqualTo(1));
            Assert.That(stream.BlockGas[0], Is.EqualTo(3 * GasCostOf.VeryLow),
                "two pushes and an add are one block charged as a single sum");
            Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.BlockFirst), "the first op of a block carries its charge");
            Assert.That(stream.Ops[0].Operand, Is.EqualTo(1UL), "PUSH1 immediates are pre-decoded into the entry");
            Assert.That(stream.Ops[1].Kind, Is.EqualTo(StreamOpKind.FusedInBlock),
                "PUSH1 2; ADD folds into a single const-op entry");
            Assert.That(stream.Ops[1].Opcode, Is.EqualTo(FusedOpcode.Add), "the pair runs under its virtual opcode");
            Assert.That(stream.Constants[(int)stream.Ops[1].Operand], Is.EqualTo((Nethermind.Int256.UInt256)2),
                "the pushed constant survives in the pool as the pair's operand");
            Assert.That(stream.Ops[^1].Kind, Is.EqualTo(StreamOpKind.Boundary),
                "STOP is not a static-cost op and must run the standard handler");
        }
    }

    [TestCase(Instruction.DUP1, 1)]
    [TestCase(Instruction.DUP16, 16)]
    public void TryBuild_DupBinaryPair_BecomesSingleEntry(Instruction dup, int depth)
    {
        byte[] code = [(byte)dup, (byte)Instruction.SUB, (byte)Instruction.STOP];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.FusedBlockFirst));
            Assert.That(stream.Ops[0].Opcode, Is.EqualTo(FusedOpcode.DupBinary));
            Assert.That(stream.Ops[0].Advance, Is.EqualTo(2));
            Assert.That(stream.Ops[0].Operand, Is.EqualTo((ulong)depth | ((ulong)(byte)Instruction.SUB << 8)));
            Assert.That(stream.PcToEntry[1], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.BlockGas[0], Is.EqualTo(GasCostOf.VeryLow + GasCostOf.VeryLow));
        }
    }

    [TestCase(Instruction.POP, FusedOpcode.PopPop, 2UL, GasCostOf.Base, false)]
    [TestCase(Instruction.POP, FusedOpcode.PopPop, 2UL, GasCostOf.Base, true)]
    [TestCase(Instruction.SWAP1, FusedOpcode.SwapPop, 2UL, GasCostOf.VeryLow, false)]
    [TestCase(Instruction.SWAP1, FusedOpcode.SwapPop, 2UL, GasCostOf.VeryLow, true)]
    [TestCase(Instruction.SWAP8, FusedOpcode.SwapPop, 9UL, GasCostOf.VeryLow, false)]
    [TestCase(Instruction.SWAP8, FusedOpcode.SwapPop, 9UL, GasCostOf.VeryLow, true)]
    public void TryBuild_FixedGasStackPair_PreservesShapeAndGas(
        Instruction first,
        byte expectedOpcode,
        ulong expectedOperand,
        ulong firstCost,
        bool hasPrefix)
    {
        byte[] code = hasPrefix
            ? [(byte)Instruction.PUSH0, (byte)first, (byte)Instruction.POP, (byte)Instruction.STOP]
            : [(byte)first, (byte)Instruction.POP, (byte)Instruction.STOP];
        int pairPc = hasPrefix ? 1 : 0;
        int pairEntry = hasPrefix ? 1 : 0;
        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.BlockGas, Has.Length.EqualTo(1));
            Assert.That(stream.BlockGas[0], Is.EqualTo(firstCost + GasCostOf.Base + (hasPrefix ? GasCostOf.Base : 0)));
            Assert.That(stream.Ops[pairEntry].Opcode, Is.EqualTo(expectedOpcode));
            Assert.That(stream.Ops[pairEntry].Operand, Is.EqualTo(expectedOperand));
            Assert.That(stream.Ops[pairEntry].Kind, Is.EqualTo(hasPrefix ? StreamOpKind.FusedInBlock : StreamOpKind.FusedBlockFirst));
            Assert.That(stream.Ops[pairEntry].Pc, Is.EqualTo(pairPc));
            Assert.That(stream.Ops[pairEntry].Advance, Is.EqualTo(2));
            Assert.That(stream.PcToEntry[pairPc], Is.EqualTo(pairEntry));
            Assert.That(stream.PcToEntry[pairPc + 1], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[pairPc + 2], Is.EqualTo(pairEntry + 1));
            Assert.That(stream.Ops[pairEntry + 1].Kind, Is.EqualTo(StreamOpKind.Boundary));
        }
    }

    [Test]
    public void TryBuild_PopRun_CoalescesConsecutivePops()
    {
        byte[] code = [(byte)Instruction.POP, (byte)Instruction.POP, (byte)Instruction.POP, (byte)Instruction.STOP];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Ops[0].Opcode, Is.EqualTo(FusedOpcode.PopPop));
            Assert.That(stream.Ops[0].Operand, Is.EqualTo(3UL));
            Assert.That(stream.Ops[0].Advance, Is.EqualTo(3));
            Assert.That(stream.BlockGas[0], Is.EqualTo(3 * GasCostOf.Base));
            Assert.That(stream.PcToEntry[0], Is.EqualTo(0));
            Assert.That(stream.PcToEntry[1], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[2], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[3], Is.EqualTo(1));
            Assert.That(stream.Ops[1].Kind, Is.EqualTo(StreamOpKind.Boundary));
        }
    }

    [TestCase(255, 2, 0)]
    [TestCase(256, 3, 1)]
    [TestCase(257, 3, 2)]
    public void TryBuild_PopRun_ChunksAtByteAdvanceBoundary(
        int popCount,
        int expectedEntryCount,
        int expectedTailCount)
    {
        byte[] code = new byte[popCount + 1];
        code.AsSpan(0, popCount).Fill((byte)Instruction.POP);
        code[^1] = (byte)Instruction.STOP;

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Ops, Has.Length.EqualTo(expectedEntryCount));
            Assert.That(stream.Ops[0].Opcode, Is.EqualTo(FusedOpcode.PopPop));
            Assert.That(stream.Ops[0].Operand, Is.EqualTo(255UL));
            Assert.That(stream.Ops[0].Advance, Is.EqualTo(byte.MaxValue));
            Assert.That(stream.BlockGas[0], Is.EqualTo((ulong)popCount * GasCostOf.Base));
            Assert.That(stream.PcToEntry[0], Is.EqualTo(0));
            for (int pc = 1; pc < byte.MaxValue; pc++)
                Assert.That(stream.PcToEntry[pc], Is.EqualTo(InstructionStream.InvalidEntry));

            if (expectedTailCount == 0)
            {
                Assert.That(stream.PcToEntry[popCount], Is.EqualTo(1));
            }
            else
            {
                Assert.That(stream.PcToEntry[255], Is.EqualTo(1));
                Assert.That(stream.Ops[1].Opcode, Is.EqualTo(expectedTailCount == 1 ? (byte)Instruction.POP : FusedOpcode.PopPop));
                Assert.That(stream.Ops[1].Operand, Is.EqualTo((ulong)(expectedTailCount == 1 ? 0 : expectedTailCount)));
                Assert.That(stream.Ops[1].Advance, Is.EqualTo(expectedTailCount == 1 ? 1 : expectedTailCount));
                if (expectedTailCount > 1)
                    Assert.That(stream.PcToEntry[256], Is.EqualTo(InstructionStream.InvalidEntry));
                Assert.That(stream.PcToEntry[popCount], Is.EqualTo(2));
            }

            Assert.That(stream.Ops[^1].Kind, Is.EqualTo(StreamOpKind.Boundary));
        }
    }

    [Test]
    public void TryBuild_MaxSizeTypicalContract_StaysWithinRetainedCap()
    {
        // A max EIP-170 (24 KiB) contract of typical output must stay within the retained-size cap.
        const int maxCodeSize = 24 * 1024;
        byte[] block =
        [
            (byte)Instruction.PUSH1, 0x07, (byte)Instruction.ADD,
            (byte)Instruction.PUSH1, 0x03, (byte)Instruction.MUL,
            (byte)Instruction.PUSH1, 0x02, (byte)Instruction.MUL,
            (byte)Instruction.PUSH1, 0x05, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
            (byte)Instruction.DUP1, (byte)Instruction.POP,
        ];
        List<byte> code = new(maxCodeSize);
        while (code.Count + block.Length < maxCodeSize)
        {
            code.AddRange(block);
        }
        code.Add((byte)Instruction.STOP);

        InstructionStream stream = InstructionStream.TryBuild(code.ToArray())!;

        Assert.That(stream, Is.Not.Null, "a max-size typical-output contract should build a retained stream");
        Assert.That(stream.RetainedBytes, Is.LessThanOrEqualTo(StreamInterpreter.MaxStreamRetainedBytes),
            $"a {code.Count}-byte contract retained {stream.RetainedBytes} bytes, over the {StreamInterpreter.MaxStreamRetainedBytes}-byte cap");
    }

    [Test]
    public void TryBuild_Jumpdest_GetsItsOwnSoloBlock()
    {
        byte[] code =
        [
            (byte)Instruction.JUMPDEST,
            (byte)Instruction.PUSH1, 0x01,
            (byte)Instruction.POP,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.BlockGas, Has.Length.EqualTo(2),
                "a fused PUSH2+JUMP lands one past the JUMPDEST, so the ops after it need a separately charged block");
            Assert.That(stream.BlockGas[0], Is.EqualTo(GasCostOf.JumpDest));
            Assert.That(stream.BlockGas[1], Is.EqualTo(GasCostOf.VeryLow + GasCostOf.Base));
        }
    }

    [Test]
    public void TryBuild_Push2JumpWithValidDest_BecomesStaticJump()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH2, 0x00, 0x05,
            (byte)Instruction.JUMP,
            (byte)Instruction.STOP,
            (byte)Instruction.JUMPDEST,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.StaticJump),
                "an analysis-validated PUSH2+JUMP pair jumps straight to its target entry");
            Assert.That(stream.Ops[0].Operand, Is.EqualTo((ulong)stream.PcToEntry[5]),
                "the operand is the pre-resolved target entry index");
            Assert.That(stream.Ops[1].Kind, Is.EqualTo(StreamOpKind.Boundary), "STOP stays a table op");
        }
    }

    [Test]
    public void TryBuild_Push2JumpToPushImmediate_RefusesToStream()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH3, 0xAA, 0x5B, 0xBB,  // 0x5B at pc 2 is an immediate, not a JUMPDEST
            (byte)Instruction.PUSH2, 0x00, 0x02,        // targets pc 2
            (byte)Instruction.JUMP,
        ];

        Assert.That(InstructionStream.TryBuild(code), Is.Null,
            "a static jump whose target is a PUSH immediate is not a real JUMPDEST; the code must run on the bytecode loop");
    }

    [TestCaseSource(nameof(BoundaryFallbackCases))]
    public void TryBuild_OpThatCannotBePrecharged_StaysBoundary(byte[] code)
        => Assert.That(InstructionStream.TryBuild(code)!.Ops[0].Kind, Is.EqualTo(StreamOpKind.Boundary),
            "an op that can't be precharged keeps the table handler and its exact semantics");

    private static IEnumerable<TestCaseData> BoundaryFallbackCases()
    {
        yield return new TestCaseData(new byte[] { (byte)Instruction.PUSH2, 0x00, 0x04, (byte)Instruction.JUMP, (byte)Instruction.STOP })
        { TestName = "Push2JumpToNonJumpdest" };
        yield return new TestCaseData(new byte[] { (byte)Instruction.PUSH4, 0x01, 0x02 })
        { TestName = "TruncatedTrailingPush" };
    }

    [Test]
    public void TryBuild_PcToEntry_MapsInstructionStartsAndRejectsImmediates()
    {
        byte[] code =
        [
            (byte)Instruction.PUSH3, 0x01, 0x02, 0x03,
            (byte)Instruction.ADD,
        ];

        InstructionStream stream = InstructionStream.TryBuild(code)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stream.PcToEntry[0], Is.EqualTo(0), "PUSH3 opens the block as its first entry");
            Assert.That(stream.Constants[(int)stream.Ops[0].Operand], Is.EqualTo((Nethermind.Int256.UInt256)0x010203),
                "PUSH3 immediates are pre-decoded big-endian into the pool");
            Assert.That(stream.Ops[0].Kind, Is.EqualTo(StreamOpKind.FusedBlockFirst),
                "PUSH3 const; ADD folds into a single block-charging const-op entry");
            Assert.That(stream.Ops[0].Advance, Is.EqualTo(5), "the pair covers the push, its immediates, and the op");
            Assert.That(stream.PcToEntry[1], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[2], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[3], Is.EqualTo(InstructionStream.InvalidEntry));
            Assert.That(stream.PcToEntry[4], Is.EqualTo(InstructionStream.InvalidEntry),
                "nothing can land inside a fused pair, so the op's own start unmaps");
            Assert.That(stream.PcToEntry[5], Is.EqualTo(stream.Ops.Length), "pc one past the end terminates the stream loop");
        }
    }

    [Test]
    public void TryBuild_EmptyCode_ReturnsNull()
        => Assert.That(InstructionStream.TryBuild(ReadOnlySpan<byte>.Empty), Is.Null);

    [TestCase(Instruction.ADD, GasCostOf.VeryLow, TestName = "Add")]
    [TestCase(Instruction.MUL, GasCostOf.Low, TestName = "Mul")]
    [TestCase(Instruction.SDIV, GasCostOf.Low, TestName = "SDiv")]
    [TestCase(Instruction.POP, GasCostOf.Base, TestName = "Pop")]
    [TestCase(Instruction.PUSH0, GasCostOf.Base, TestName = "Push0")]
    [TestCase(Instruction.SHL, GasCostOf.VeryLow, TestName = "Shl")]
    [TestCase(Instruction.DUP8, GasCostOf.VeryLow, TestName = "Dup8")]
    [TestCase(Instruction.DUP16, GasCostOf.VeryLow, TestName = "Dup16")]
    [TestCase(Instruction.SWAP8, GasCostOf.VeryLow, TestName = "Swap8")]
    [TestCase(Instruction.PUSH32, GasCostOf.VeryLow, TestName = "Push32_ViaConstantPool")]
    public void GetInBlockCost_ForStaticCostOp_MatchesGasCostOf(Instruction instruction, ulong expectedCost)
        => Assert.That(InstructionStream.GetInBlockCost(instruction), Is.EqualTo(expectedCost),
            "block sums diverging from GasCostOf is a consensus bug");

    [TestCase(Instruction.PUSH2, TestName = "Push2_KeepsFusedTableHandler")]
    [TestCase(Instruction.SWAP9, TestName = "Swap9_OutsideExecutorSwitch")]
    [TestCase(Instruction.MLOAD, TestName = "MLoad_DynamicMemoryGas")]
    [TestCase(Instruction.SLOAD, TestName = "SLoad_DynamicAccessGas")]
    [TestCase(Instruction.GAS, TestName = "Gas_MustObserveExactRemaining")]
    [TestCase(Instruction.EXP, TestName = "Exp_DynamicGas")]
    public void GetInBlockCost_ForExcludedOp_ReturnsNotInBlock(Instruction instruction)
        => Assert.That(InstructionStream.GetInBlockCost(instruction), Is.EqualTo(InstructionStream.NotInBlock));
}

// Mutates process-wide StreamInterpreter statics, so it must not run alongside other EVM tests.
[TestFixture, NonParallelizable]
public class StreamInterpreterDifferentialTests : VirtualMachineTestsBase
{
    // The stream only engages on tip-fork fingerprints; the base default would compare the loop to itself.
    protected override ForkActivation Activation => MainnetSpecProvider.OsakaActivation;

    private static readonly byte[] ArithmeticChain = Prepare.EvmCode
        .PushData(7).PushData(5).Op(Instruction.ADD)
        .PushData(3).Op(Instruction.MUL)
        .PushData(2).Op(Instruction.SWAP1).Op(Instruction.DIV)
        .Op(Instruction.DUP1).Op(Instruction.EQ)
        .Op(Instruction.ISZERO)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] DupBinaryAdd = Prepare.EvmCode
        .PushData(7).Op(Instruction.DUP1).Op(Instruction.ADD)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] DupBinarySubOrder = Prepare.EvmCode
        .PushData(3).PushData(10).Op(Instruction.DUP2).Op(Instruction.SUB)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] DupBinaryUnderflow =
        [(byte)Instruction.DUP2, (byte)Instruction.ADD, (byte)Instruction.STOP];

    private static readonly byte[] FullStackDupBinary = BuildFullStackDupBinary();
    private static readonly byte[] SwapPopSuccess = BuildSwapPopCode(8, succeeds: true);
    private static readonly byte[] SwapPopUnderflow = BuildSwapPopCode(8, succeeds: false);
    private static readonly byte[] PopPopSuccess =
        [(byte)Instruction.PUSH0, (byte)Instruction.PUSH0, (byte)Instruction.PUSH0,
            (byte)Instruction.POP, (byte)Instruction.POP, (byte)Instruction.POP, (byte)Instruction.STOP];

    private static readonly byte[] JumpLoop = Prepare.EvmCode
        .PushData(5)                                  // counter
        .Op(Instruction.JUMPDEST)                     // pc 2: loop head
        .PushData(1).Op(Instruction.SWAP1).Op(Instruction.SUB)
        .Op(Instruction.DUP1)
        .PushData(2).Op(Instruction.JUMPI)            // loop while counter != 0
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] StoreAndReturn = Prepare.EvmCode
        .PushData(42).PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] StackUnderflow = Prepare.EvmCode
        .Op(Instruction.ADD)
        .Done;

    private static readonly byte[] NestedCallWithResume = Prepare.EvmCode
        .Call(CalleeAddress, 50000)
        .PushData(3).Op(Instruction.MUL)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] CreateWithInitCode = Prepare.EvmCode
        .Create(Prepare.EvmCode
            .PushData(1).PushData(2).Op(Instruction.ADD).Op(Instruction.POP)
            .Op(Instruction.STOP)
            .Done, 0)
        .Op(Instruction.POP)
        .Op(Instruction.STOP)
        .Done;

    private static readonly byte[] FusedPush2JumpiLoop =
    [
        (byte)Instruction.PUSH1, 200,                  // counter
        (byte)Instruction.JUMPDEST,                    // pc 2
        (byte)Instruction.PUSH1, 1,
        (byte)Instruction.SWAP1,
        (byte)Instruction.SUB,
        (byte)Instruction.DUP1,
        (byte)Instruction.PUSH2, 0x00, 0x02,
        (byte)Instruction.JUMPI,
        (byte)Instruction.STOP,
    ];

    // Code ends inside PUSH2's immediates: pc runs past end-of-code; the stream must still exit cleanly.
    private static readonly byte[] TruncatedTrailingPush2 =
    [
        (byte)Instruction.PUSH1, 1,
        (byte)Instruction.PUSH2, 0x00,
    ];

    private static readonly byte[] DeepStackToTheLimit = BuildDeepStackCode();

    private static byte[] BuildFullStackDupBinary()
    {
        byte[] code = new byte[1027];
        code.AsSpan(0, 1024).Fill((byte)Instruction.PUSH0);
        code[1024] = (byte)Instruction.DUP1;
        code[1025] = (byte)Instruction.ADD;
        code[1026] = (byte)Instruction.STOP;
        return code;
    }

    private static byte[] BuildSwapPopCode(int swapDepth, bool succeeds)
    {
        List<byte> code = [];
        int stackDepth = succeeds ? swapDepth + 1 : swapDepth;
        for (int value = 1; value <= stackDepth; value++)
        {
            code.Add((byte)Instruction.PUSH1);
            code.Add((byte)value);
        }

        code.Add((byte)((byte)Instruction.SWAP1 + swapDepth - 1));
        code.Add((byte)Instruction.POP);
        if (!succeeds)
        {
            code.Add((byte)Instruction.STOP);
            return code.ToArray();
        }

        if (swapDepth > 1)
            code.Add((byte)((byte)Instruction.SWAP1 + swapDepth - 2));
        code.Add((byte)Instruction.PUSH0);
        code.Add((byte)Instruction.MSTORE);
        code.Add((byte)Instruction.PUSH1);
        code.Add(32);
        code.Add((byte)Instruction.PUSH0);
        code.Add((byte)Instruction.RETURN);
        return code.ToArray();
    }

    private static byte[] BuildDeepStackCode()
    {
        // 1024 pushes fill the stack exactly to the limit; the 1025th must overflow.
        byte[] code = new byte[1025 + 1];
        code.AsSpan(0, 1025).Fill((byte)Instruction.PUSH0);
        code[1025] = (byte)Instruction.STOP;
        return code;
    }

    // PUSH; AND fuses to a bitwise superinstruction that indexes ConstantBytes — exercises the path that
    // only allocates ConstantBytes when a bitwise fusion is actually emitted.
    private static readonly byte[] BitwiseFusionWithConstant = Prepare.EvmCode
        .PushData(0xFF)
        .PushData(0x0F)
        .Op(Instruction.AND)
        .PushData(0)
        .Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    private static readonly byte[] FullStackStaticJump = BuildFullStackJump(Instruction.JUMP);
    private static readonly byte[] FullStackStaticJumpI = BuildFullStackJump(Instruction.JUMPI);

    private static byte[] BuildFullStackJump(Instruction jump)
    {
        // 1024 PUSH0 fill the stack to the limit, so the fused PUSH2 overflows on its own push exactly as the
        // unfused PUSH2 does — the jump must never execute. The PUSH2 immediate targets the JUMPDEST so the
        // analyzer fuses PUSH2+JUMP/JUMPI into StaticJump/StaticJumpI, which is the path under test.
        const int fill = 1024;
        byte[] code = new byte[fill + 6];
        code.AsSpan(0, fill).Fill((byte)Instruction.PUSH0);
        int dest = fill + 4;                       // offset of the JUMPDEST
        code[fill] = (byte)Instruction.PUSH2;
        code[fill + 1] = (byte)(dest >> 8);
        code[fill + 2] = (byte)dest;
        code[fill + 3] = (byte)jump;
        code[fill + 4] = (byte)Instruction.JUMPDEST;
        code[fill + 5] = (byte)Instruction.STOP;
        return code;
    }

    private static Address CalleeAddress => TestItem.AddressC;
    private static Address SolidityExampleAddress => TestItem.AddressE;

    // Runtime code of Legacy stExample.solidityExample — smallest real-world reproducer of the
    // mainnet stream divergence (CREATE child contract, then CALL into it).
    private const string SolidityExampleRuntime =
        "608060405234801561001057600080fd5b506004361061002b5760003560e01c8063b66176a714610030575b600080fd5b61004a6004803603810190610045919061018d565b61004c565b005b60405161005890610145565b604051809103906000f080158015610074573d6000803e3d6000fd5b506000806101000a81548173ffffffffffffffffffffffffffffffffffffffff021916908373ffffffffffffffffffffffffffffffffffffffff16021790555060008054906101000a900473ffffffffffffffffffffffffffffffffffffffff1673ffffffffffffffffffffffffffffffffffffffff1663b66176a783836040518363ffffffff1660e01b815260040161010f9291906101dc565b600060405180830381600087803b15801561012957600080fd5b505af115801561013d573d6000803e3d6000fd5b505050505050565b6101238061020683390190565b600080fd5b6000819050919050565b61016a81610157565b811461017557600080fd5b50565b60008135905061018781610161565b92915050565b600080604083850312156101a4576101a3610152565b5b60006101b285828601610178565b92505060206101c385828601610178565b9150509250929050565b6101d681610157565b82525050565b60006040820190506101f160008301856101cd565b6101fe60208301846101cd565b939250505056fe608060405267ff00ff00ff00ff0060005534801561001c57600080fd5b5060f88061002b6000396000f3fe6080604052348015600f57600080fd5b506004361060285760003560e01c8063b66176a714602d575b600080fd5b60436004803603810190603f91906089565b6045565b005b806000819055508082555050565b600080fd5b6000819050919050565b6069816058565b8114607357600080fd5b50565b6000813590506083816062565b92915050565b60008060408385031215609d57609c6053565b5b600060a9858286016076565b925050602060b8858286016076565b915050925092905056fea26469706673582212209beb73a466a9b6fcce247e8e1ec0ac303febcb2192064276aa2188d57d06a98d64736f6c63430008150033a2646970667358221220223335c3b4079496a81c6cbdfc0adb8a4b8ed0637499a9301f31c89383d238e164736f6c63430008150033";

    private static readonly byte[] SolidityExampleCall = Prepare.EvmCode
        .CallWithInput(SolidityExampleAddress, 4_000_000,
            Core.Extensions.Bytes.FromHexString(
                "b66176a700000000000000000000000000000000000000000000000000000000000000050000000000000000000000000000000000000000000000000000000000000045"))
        .Op(Instruction.STOP)
        .Done;

    private static byte[] BuildOutOfGasMidBlock()
    {
        // ~2000 in-block ops behind a gas limit that can't cover them: must die metered at the exact per-op point.
        byte[] code = new byte[2001];
        code[0] = (byte)Instruction.PUSH0;
        for (int i = 1; i < 2000; i++)
        {
            code[i] = (byte)Instruction.DUP1;
        }

        code[2000] = (byte)Instruction.STOP;
        return code;
    }

    public static IEnumerable<TestCaseData> DifferentialCases()
    {
        yield return new TestCaseData(ArithmeticChain) { TestName = "ArithmeticChain" };
        yield return new TestCaseData(DupBinaryAdd) { TestName = "FusedDupBinaryAdd" };
        yield return new TestCaseData(DupBinarySubOrder) { TestName = "FusedDupBinarySubPreservesOrder" };
        yield return new TestCaseData(DupBinaryUnderflow) { TestName = "FusedDupBinaryUnderflow" };
        yield return new TestCaseData(FullStackDupBinary) { TestName = "FusedDupBinaryOverflow" };
        yield return new TestCaseData(SwapPopSuccess) { TestName = "FusedSwapPopSuccess" };
        yield return new TestCaseData(SwapPopUnderflow) { TestName = "FusedSwapPopUnderflow" };
        yield return new TestCaseData(PopPopSuccess) { TestName = "FusedPopPopSuccess" };
        yield return new TestCaseData(JumpLoop) { TestName = "JumpLoopWithFusedPush" };
        yield return new TestCaseData(StoreAndReturn) { TestName = "MemoryBoundaryOpsAndReturn" };
        yield return new TestCaseData(StackUnderflow) { TestName = "StackUnderflowFailure" };
        yield return new TestCaseData(NestedCallWithResume) { TestName = "NestedCallWithResume" };
        yield return new TestCaseData(CreateWithInitCode) { TestName = "CreateWithInitCode" };
        yield return new TestCaseData(FusedPush2JumpiLoop) { TestName = "FusedPush2JumpiLoop" };
        yield return new TestCaseData(TruncatedTrailingPush2) { TestName = "TruncatedTrailingPush2" };
        yield return new TestCaseData(DeepStackToTheLimit) { TestName = "DeepStackToTheLimit" };
        yield return new TestCaseData(FullStackStaticJump) { TestName = "FullStackStaticJumpOverflows" };
        yield return new TestCaseData(FullStackStaticJumpI) { TestName = "FullStackStaticJumpIOverflows" };
        yield return new TestCaseData(BitwiseFusionWithConstant) { TestName = "BitwiseFusionWithConstant" };
        yield return new TestCaseData(SolidityExampleCall) { TestName = "SolidityExampleCreateAndCall" };
        yield return new TestCaseData(BuildOutOfGasMidBlock()) { TestName = "OutOfGasInsideMeteredBlock" };
    }

    [Test]
    public void Activation_IsShanghaiOrLater_SoStreamEngages() =>
        Assert.That(
            SpecProvider.GetSpec(Activation).IncludePush0Instruction,
            "the differential fixture must run on a fork where the stream engages");

    [TestCase(Architecture.Arm64, false)]
    [TestCase(Architecture.X64, true)]
    public void StreamInterpreter_DefaultEnablement_IsArchitectureSpecific(Architecture architecture, bool expected) =>
        Assert.That(StreamInterpreter.IsEnabledByDefault(architecture), Is.EqualTo(expected));

    private static readonly byte[] CalleeCode = Prepare.EvmCode
        .PushData(7).PushData(6).Op(Instruction.MUL)
        .PushData(0).Op(Instruction.MSTORE)
        .PushData(32).PushData(0).Op(Instruction.RETURN)
        .Done;

    [TestCaseSource(nameof(DifferentialCases))]
    public void StreamInterpreter_ComparedToByteCodeLoop_IsObservablyIdentical(byte[] code)
    {
        ReceiptCaptureTracer baseline = RunWithInterpreter(code, useStream: false);
        // Baseline run commits state; reset so the stream run sees the same starting world.
        Setup();

        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(code, useStream: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore),
                "the stream interpreter did not engage — this differential proved nothing");
            Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode), "success/failure must match");
            Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent), "gas must match to the unit");
            Assert.That(streamed.Output, Is.EqualTo(baseline.Output), "return data must match");
        }
    }

    // Guards that the executor's in-block switch covers exactly the TryGetInBlockCost set:
    // a precharged op with no case returns BadInstruction and diverges here.
    [TestCaseSource(nameof(InBlockOpcodes))]
    public void StreamExecutor_DispatchesEveryInBlockOpcode_IdenticallyToBytecodeLoop(Instruction op)
    {
        List<byte> code = [];
        for (int i = 0; i < 9; i++) { code.Add((byte)Instruction.PUSH1); code.Add(0x01); }
        code.Add((byte)op);
        if (op is >= Instruction.PUSH1 and <= Instruction.PUSH32)
            for (int i = 0; i <= op - Instruction.PUSH1; i++) code.Add(0x01);
        code.Add((byte)Instruction.STOP);
        byte[] bytecode = code.ToArray();

        ReceiptCaptureTracer baseline = RunWithInterpreter(bytecode, useStream: false);
        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(bytecode, useStream: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore), $"{op}: stream did not engage");
            Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode), $"{op}: status must match (no BadInstruction)");
            Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent), $"{op}: gas must match");
            Assert.That(streamed.Output, Is.EqualTo(baseline.Output), $"{op}: output must match");
        }
    }

    private static IEnumerable<Instruction> InBlockOpcodes()
    {
        for (int op = 0; op <= byte.MaxValue; op++)
            if (InstructionStream.GetInBlockCost((Instruction)op) != InstructionStream.NotInBlock)
                yield return (Instruction)op;
    }

    // The stream's gas guards must use IsOutOfGas (gas is ulong, so "< 0" is always false). With a dead
    // guard the stream runs past exhaustion and reports success, diverging from the bytecode loop here.
    private static IEnumerable<TestCaseData> OutOfGasCases()
    {
        // Boundary op: PUSH1 0; SLOAD; STOP — the ~100-gas budget can't pay the cold SLOAD (2100).
        yield return new TestCaseData(new byte[] { (byte)Instruction.PUSH1, 0x00, (byte)Instruction.SLOAD, (byte)Instruction.STOP }, 21_100UL) { TestName = "OutOfGasOnBoundarySLoad" };

        // Metered fallback: a 500-PUSH0 block (cost 1000) behind a ~500-gas budget can't be precharged,
        // so it dispatches per-op metered and exhausts mid-block.
        byte[] meteredBlock = new byte[501];
        for (int i = 0; i < 500; i++) meteredBlock[i] = (byte)Instruction.PUSH0;
        meteredBlock[500] = (byte)Instruction.STOP;
        yield return new TestCaseData(meteredBlock, 21_500UL) { TestName = "OutOfGasInMeteredFallback" };
    }

    [TestCaseSource(nameof(OutOfGasCases))]
    public void StreamInterpreter_OutOfGas_MatchesByteCodeLoop(byte[] code, ulong gasLimit)
    {
        ReceiptCaptureTracer baseline = RunWithInterpreter(code, useStream: false, gasLimit: gasLimit);
        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(code, useStream: true, gasLimit: gasLimit);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore), "the stream did not engage");
            Assert.That(baseline.StatusCode, Is.EqualTo((byte)Evm.StatusCode.Failure), "precondition: the bytecode loop must run out of gas");
            Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode), "out-of-gas status must match the bytecode loop");
            Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent), "gas spent on out-of-gas must match the bytecode loop");
        }
    }

    [Test]
    public void StreamInterpreter_DupBinary_OpcodeCountMatchesOnFailureAndSuccess()
    {
        RunWithInterpreter(DupBinaryUnderflow, useStream: false);
        int baselineUnderflow = Machine.OpCodeCount;

        Setup();
        RunWithInterpreter(DupBinaryUnderflow, useStream: true);
        int streamUnderflow = Machine.OpCodeCount;

        Setup();
        RunWithInterpreter(DupBinaryAdd, useStream: false);
        int baselineSuccess = Machine.OpCodeCount;

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        RunWithInterpreter(DupBinaryAdd, useStream: true);
        int streamSuccess = Machine.OpCodeCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineUnderflow, Is.EqualTo(1), "a failed DUP must be the only completed opcode");
            Assert.That(streamUnderflow, Is.EqualTo(baselineUnderflow));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
            Assert.That(streamSuccess, Is.EqualTo(baselineSuccess));
        }
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    public void StreamInterpreter_DupBinary_PollsBeforeSecondOpcodeOnlyAfterDupSucceeds(int stackDepth, bool shouldCancel)
    {
        byte[] code = BuildDupBinaryAtCancellationBoundary(stackDepth);
        bool hasFusedPair = false;
        foreach (StreamOp op in InstructionStream.TryBuild(code)!.Ops)
            hasFusedPair |= op.Opcode == FusedOpcode.DupBinary;
        Assert.That(hasFusedPair, Is.True);

        PollingCancellationTracer baselineTracer = new();
        int baselineCount = AssertCancellationOutcome(code, useStream: false, shouldCancel, baselineTracer);

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        PollingCancellationTracer streamTracer = new();
        int streamCount = AssertCancellationOutcome(code, useStream: true, shouldCancel, streamTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(streamTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
            Assert.That(streamCount, Is.EqualTo(baselineCount));
        }
    }

    [TestCase(0, 2, 1)]
    [TestCase(1, 2, 3)]
    [TestCase(2, 2, 5)]
    [TestCase(0, 3, 1)]
    [TestCase(1, 3, 3)]
    [TestCase(2, 3, 5)]
    [TestCase(3, 3, 7)]
    public void StreamInterpreter_PopRun_PreservesFailureOrderAndOpcodeCount(
        int stackDepth,
        int popCount,
        int expectedCount)
    {
        byte[] code = BuildPopPopCode(stackDepth, popCount);
        bool hasPopRun = false;
        foreach (StreamOp op in InstructionStream.TryBuild(code)!.Ops)
            hasPopRun |= op.Opcode == FusedOpcode.PopPop;
        Assert.That(hasPopRun, Is.True);

        RunWithInterpreter(code, useStream: false);
        int baselineCount = Machine.OpCodeCount;

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        RunWithInterpreter(code, useStream: true);
        int streamCount = Machine.OpCodeCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineCount, Is.EqualTo(expectedCount));
            Assert.That(streamCount, Is.EqualTo(baselineCount));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
        }
    }

    [TestCase(false, 9)]
    [TestCase(true, 17)]
    public void StreamInterpreter_SwapPop_PreservesStackResultAndOpcodeCount(bool succeeds, int expectedCount)
    {
        byte[] code = BuildSwapPopCode(8, succeeds);
        Assert.That(InstructionStream.TryBuild(code)!.Ops, Has.Some.Matches<StreamOp>(op => op.Opcode == FusedOpcode.SwapPop));

        ReceiptCaptureTracer baseline = RunWithInterpreter(code, useStream: false);
        int baselineCount = Machine.OpCodeCount;

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        ReceiptCaptureTracer streamed = RunWithInterpreter(code, useStream: true);
        int streamCount = Machine.OpCodeCount;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineCount, Is.EqualTo(expectedCount));
            Assert.That(streamCount, Is.EqualTo(baselineCount));
            Assert.That(streamed.StatusCode, Is.EqualTo(baseline.StatusCode));
            Assert.That(streamed.GasSpent, Is.EqualTo(baseline.GasSpent));
            Assert.That(streamed.Output, Is.EqualTo(baseline.Output));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
        }
    }

    private static byte[] BuildPopPopCode(int stackDepth, int popCount)
    {
        List<byte> code = [];
        for (int i = 0; i < stackDepth; i++)
            code.Add((byte)Instruction.PUSH0);
        for (int i = 0; i < popCount; i++)
            code.Add((byte)Instruction.POP);
        code.Add((byte)Instruction.STOP);
        return code.ToArray();
    }

    [TestCase(Instruction.POP, Instruction.POP, 0, false)]
    [TestCase(Instruction.POP, Instruction.POP, 1, true)]
    [TestCase(Instruction.SWAP8, Instruction.POP, 8, false)]
    [TestCase(Instruction.SWAP8, Instruction.POP, 9, true)]
    public void StreamInterpreter_FixedGasPair_PollsBeforeSecondOpcodeOnlyAfterFirstSucceeds(
        Instruction first,
        Instruction second,
        int stackDepth,
        bool shouldCancel)
    {
        byte[] code = BuildFixedGasPairAtCancellationBoundary(first, second, stackDepth);
        byte expectedOpcode = first == Instruction.POP ? FusedOpcode.PopPop : FusedOpcode.SwapPop;
        bool hasFusedPair = false;
        foreach (StreamOp op in InstructionStream.TryBuild(code)!.Ops)
            hasFusedPair |= op.Opcode == expectedOpcode;
        Assert.That(hasFusedPair, Is.True);

        PollingCancellationTracer baselineTracer = new();
        int baselineCount = AssertCancellationOutcome(code, useStream: false, shouldCancel, baselineTracer);

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        PollingCancellationTracer streamTracer = new();
        int streamCount = AssertCancellationOutcome(code, useStream: true, shouldCancel, streamTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(streamTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
            Assert.That(streamCount, Is.EqualTo(baselineCount));
        }
    }

    [TestCase(0, 3, false)]
    [TestCase(1, 3, true)]
    [TestCase(2, 3, true)]
    [TestCase(3, 3, true)]
    public void StreamInterpreter_PopRun_PollsAtEachOriginalOpcodeBoundary(
        int stackDepth,
        int popCount,
        bool shouldCancel)
    {
        byte[] code = BuildPopRunAtCancellationBoundary(stackDepth, popCount);
        bool hasPopRun = false;
        foreach (StreamOp op in InstructionStream.TryBuild(code)!.Ops)
            hasPopRun |= op.Opcode == FusedOpcode.PopPop && op.Operand == (ulong)popCount;
        Assert.That(hasPopRun, Is.True);

        PollingCancellationTracer baselineTracer = new();
        int baselineCount = AssertCancellationOutcome(code, useStream: false, shouldCancel, baselineTracer);

        Setup();
        long framesBefore = StreamInterpreter.FramesExecuted;
        PollingCancellationTracer streamTracer = new();
        int streamCount = AssertCancellationOutcome(code, useStream: true, shouldCancel, streamTracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(baselineTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(streamTracer.CancellationPollCount, Is.EqualTo(shouldCancel ? 2 : 1));
            Assert.That(StreamInterpreter.FramesExecuted, Is.GreaterThan(framesBefore));
            Assert.That(streamCount, Is.EqualTo(baselineCount));
        }
    }

    private static byte[] BuildFixedGasPairAtCancellationBoundary(Instruction first, Instruction second, int stackDepth)
    {
        const int instructionsBeforePair = 1023;
        byte[] code = new byte[instructionsBeforePair + 3];
        int jumpDestCount = instructionsBeforePair - stackDepth;
        code.AsSpan(0, jumpDestCount).Fill((byte)Instruction.JUMPDEST);
        code.AsSpan(jumpDestCount, stackDepth).Fill((byte)Instruction.PUSH0);
        code[instructionsBeforePair] = (byte)first;
        code[instructionsBeforePair + 1] = (byte)second;
        code[instructionsBeforePair + 2] = (byte)Instruction.STOP;
        return code;
    }

    private static byte[] BuildPopRunAtCancellationBoundary(int stackDepth, int popCount)
    {
        const int instructionsBeforeRun = 1023;
        byte[] code = new byte[instructionsBeforeRun + popCount + 1];
        int jumpDestCount = instructionsBeforeRun - stackDepth;
        code.AsSpan(0, jumpDestCount).Fill((byte)Instruction.JUMPDEST);
        code.AsSpan(jumpDestCount, stackDepth).Fill((byte)Instruction.PUSH0);
        code.AsSpan(instructionsBeforeRun, popCount).Fill((byte)Instruction.POP);
        code[^1] = (byte)Instruction.STOP;
        return code;
    }

    private static byte[] BuildDupBinaryAtCancellationBoundary(int stackDepth)
    {
        const int instructionsBeforePair = 1023;
        byte[] code = new byte[instructionsBeforePair + 3];
        int jumpDestCount = instructionsBeforePair - stackDepth;
        code.AsSpan(0, jumpDestCount).Fill((byte)Instruction.JUMPDEST);
        code.AsSpan(jumpDestCount, stackDepth).Fill((byte)Instruction.PUSH0);
        code[instructionsBeforePair] = (byte)(stackDepth == 0 ? Instruction.DUP2 : Instruction.DUP1);
        code[instructionsBeforePair + 1] = (byte)Instruction.ADD;
        code[instructionsBeforePair + 2] = (byte)Instruction.STOP;
        return code;
    }

    private int AssertCancellationOutcome(byte[] code, bool useStream, bool shouldCancel, Evm.Tracing.ITxTracer tracer)
    {
        if (shouldCancel)
            Assert.Throws<OperationCanceledException>(() => RunWithInterpreter(code, useStream, tracer: tracer));
        else
            Assert.DoesNotThrow(() => RunWithInterpreter(code, useStream, tracer: tracer));

        return Machine.OpCodeCount;
    }

    private ReceiptCaptureTracer RunWithInterpreter(
        byte[] code,
        bool useStream,
        ulong gasLimit = 8_000_000,
        Evm.Tracing.ITxTracer? tracer = null)
    {
        TestState.CreateAccount(CalleeAddress, 1000000);
        TestState.InsertCode(CalleeAddress, CalleeCode, Spec);
        TestState.CreateAccount(SolidityExampleAddress, 1000000);
        TestState.InsertCode(SolidityExampleAddress, Core.Extensions.Bytes.FromHexString(SolidityExampleRuntime), Spec);

        bool enabledBefore = StreamInterpreter.Enabled;
        int thresholdBefore = StreamInterpreter.BuildThreshold;
        bool forceBefore = StreamInterpreter.ForceAllContexts;
        StreamInterpreter.Enabled = useStream;
        // The base Execute path runs a non-cancelable tracer, so the production gate would skip the stream;
        // force it on here to exercise the stream regardless of the call-context heuristic.
        StreamInterpreter.ForceAllContexts = useStream;
        try
        {
            // Base Execute helper caps gas at 100k; the CREATE-heavy cases need more.
            (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);

            if (useStream)
            {
                // Production builds the stream asynchronously past a threshold this single run never
                // reaches; lower it and wait for the background build to publish on this CodeInfo.
                StreamInterpreter.BuildThreshold = 1;
                CodeInfo codeInfo = CodeInfoRepository.GetCachedCodeInfo(Recipient, Spec);
                if (!System.Threading.SpinWait.SpinUntil(() => codeInfo.GetOrBuildStream() is not null, TimeSpan.FromSeconds(5)))
                    Assert.Fail("the stream did not build within the timeout");
            }

            ReceiptCaptureTracer receiptTracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer ?? receiptTracer);
            return receiptTracer;
        }
        finally
        {
            StreamInterpreter.Enabled = enabledBefore;
            StreamInterpreter.BuildThreshold = thresholdBefore;
            StreamInterpreter.ForceAllContexts = forceBefore;
        }
    }

    private sealed class PollingCancellationTracer : Evm.Tracing.TxTracer, Evm.Tracing.ITxTracer
    {
        private int _cancellationPollCount;

        public int CancellationPollCount => _cancellationPollCount;
        public bool IsCancelable => true;
        public bool IsCancelled => _cancellationPollCount++ > 0;
    }

    private sealed class ReceiptCaptureTracer : Evm.Tracing.TxTracer
    {
        public byte StatusCode { get; private set; }
        public ulong GasSpent { get; private set; }
        public byte[] Output { get; private set; } = [];

        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Success;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            StatusCode = Evm.StatusCode.Failure;
            GasSpent = gasSpent.SpentGas;
            Output = output;
        }
    }
}
