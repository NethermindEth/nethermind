// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis;

/// <summary>
/// Randomized differential over the opcode alphabet the block analyzer treats specially: jump
/// targets, static and dynamic jumps, the fused glue pairs, constant folds, the peephole-consumed
/// shapes and the boundary ops that keep a block open. The hand-written differential cases cover
/// the shapes we thought of; a cross-client gas mismatch showed that the shapes we did not think
/// of are the ones that break, so this walks the space instead. Every generated program runs on
/// both interpreters twice - once with ample gas and once with a seed-derived budget that starves
/// it mid-run, which is what forces the metered fallback and the out-of-gas edges - and gas,
/// status and output must all match exactly. Gas alone cannot see a wrong value that does not
/// flip a branch, so the top of the stack is returned as output. A seed that fails prints its
/// bytecode, which is the reproduction.
/// </summary>
[TestFixture]
public class StreamGasFuzzTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber + 4;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
    protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

    private const int Programs = 400;
    private const ulong AmpleGas = 2_000_000;

    [Test]
    public void StreamExecution_MatchesByteCodeLoop_OverRandomJumpHeavyPrograms()
    {
        List<string> failures = [];
        for (int seed = 0; seed < Programs; seed++)
        {
            byte[] code = Generate(seed);

            ExecutionCapture baseline = RunFor(code, useStream: false, AmpleGas);
            ExecutionCapture streamed = RunFor(code, useStream: true, AmpleGas);
            Compare(failures, seed, "ample", code, baseline, streamed);

            // A budget cut somewhere inside the run starves a block precharge, so the tail executes
            // metered from raw code - the paths a run that always completes never crosses.
            if (baseline.GasSpent > GasCostOf.Transaction)
            {
                ulong execGas = baseline.GasSpent - GasCostOf.Transaction;
                ulong budget = GasCostOf.Transaction + execGas * (ulong)(seed % 97) / 97;
                ExecutionCapture tightBaseline = RunFor(code, useStream: false, budget);
                ExecutionCapture tightStreamed = RunFor(code, useStream: true, budget);
                Compare(failures, seed, $"tight budget {budget}", code, tightBaseline, tightStreamed);
            }

            if (failures.Count >= 5)
                break;
        }

        if (failures.Count > 0)
        {
            StringBuilder message = new($"{failures.Count} run(s) diverged between the two interpreters:");
            foreach (string failure in failures)
            {
                message.AppendLine().Append("  ").Append(failure);
            }

            Assert.Fail(message.ToString());
        }
    }

    private static void Compare(List<string> failures, int seed, string label, byte[] code, ExecutionCapture baseline, ExecutionCapture streamed)
    {
        if (baseline.GasSpent != streamed.GasSpent || baseline.StatusCode != streamed.StatusCode || !baseline.Output.AsSpan().SequenceEqual(streamed.Output))
        {
            failures.Add(
                $"seed {seed} ({label}): gas {baseline.GasSpent}/{streamed.GasSpent}, status {baseline.StatusCode}/{streamed.StatusCode}, " +
                $"output 0x{Convert.ToHexString(baseline.Output)}/0x{Convert.ToHexString(streamed.Output)}, code 0x{Convert.ToHexString(code)}");
        }
    }

    /// <summary>
    /// Builds a program whose jump targets are always real JUMPDESTs, so the run exercises jump
    /// accounting rather than invalid-destination failures, and whose backward jumps are all
    /// conditional on a decrementing counter so it terminates. Weighted toward the constructs the
    /// analyzer rewrites. Ends by returning the top of the stack, so a wrong value diverges even
    /// when it never flips a branch or moves gas.
    /// </summary>
    private static byte[] Generate(int seed)
    {
        Random random = new(seed);
        List<byte> code = [];
        List<int> jumpDests = [];

        // A leading counter lets generated loops decrement toward zero instead of spinning, and a
        // dozen words under it give the deep DUP and SWAP forms something to reach - the first
        // version of this generator only ever produced depth one and two, which is why it passed
        // while a permutation-coalescing bug that only shows past that depth broke every real call.
        code.AddRange([(byte)Instruction.PUSH1, (byte)(1 + random.Next(3))]);
        for (int i = 0; i < 12; i++)
        {
            code.AddRange([(byte)Instruction.PUSH1, (byte)(0x40 + i)]);
        }

        byte[] wide = new byte[32];
        int slots = 6 + random.Next(18);
        for (int i = 0; i < slots; i++)
        {
            switch (random.Next(20))
            {
                case 0:
                    jumpDests.Add(code.Count);
                    code.Add((byte)Instruction.JUMPDEST);
                    break;
                case 1:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256)]);
                    break;
                case 2:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.ADD]);
                    break;
                case 3:
                    code.Add((byte)((byte)Instruction.DUP1 + random.Next(8)));
                    break;
                case 4:
                    code.AddRange([(byte)((byte)Instruction.DUP1 + random.Next(8)), (byte)Instruction.POP]);
                    break;
                case 5:
                    code.Add((byte)Instruction.ISZERO);
                    break;
                case 6:
                    code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.ISZERO]);
                    break;
                case 7:
                    // A run of several permutation ops at mixed depths, the shape the coalescing pass
                    // rewrites and the shape the first generator never produced.
                    for (int k = random.Next(2, 6); k > 0; k--)
                    {
                        code.Add(random.Next(3) switch
                        {
                            0 => (byte)((byte)Instruction.DUP1 + random.Next(8)),
                            1 => (byte)((byte)Instruction.SWAP1 + random.Next(8)),
                            _ => (byte)Instruction.POP,
                        });
                    }

                    break;
                case 8:
                    code.AddRange([(byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x00, (byte)Instruction.MSTORE]);
                    break;
                case 9 when jumpDests.Count > 0:
                    // Static conditional jump: PUSH2 target + JUMPI, the fused shape.
                    int condDest = jumpDests[random.Next(jumpDests.Count)];
                    code.AddRange([(byte)Instruction.DUP1, (byte)Instruction.ISZERO, (byte)Instruction.PUSH2, (byte)(condDest >> 8), (byte)condDest, (byte)Instruction.JUMPI]);
                    break;
                case 10 when jumpDests.Count > 0:
                    // Dynamic conditional jump: the target arrives through the stack.
                    int dynDest = jumpDests[random.Next(jumpDests.Count)];
                    code.AddRange([(byte)Instruction.PUSH1, 0x00, (byte)Instruction.PUSH2, (byte)(dynDest >> 8), (byte)dynDest, (byte)Instruction.SWAP1, (byte)Instruction.JUMPI]);
                    break;
                case 11:
                    // AND alone and AND feeding ISZERO, the fused compare-to-zero pair.
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.AND]);
                    if (random.Next(2) == 0) code.Add((byte)Instruction.ISZERO);
                    break;
                case 12:
                    // Division and modulo, with a zero divisor often enough to hit that fold.
                    code.AddRange([
                        (byte)Instruction.PUSH1, (byte)(random.Next(4) == 0 ? 0 : random.Next(256)),
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)(random.Next(2) == 0 ? Instruction.DIV : Instruction.MOD)]);
                    break;
                case 13:
                    // Shifts, sometimes past 255 so saturation folds and cores agree.
                    if (random.Next(3) == 0)
                        code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH2, 0x01, 0x00]);
                    else
                        code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(256)]);
                    code.Add((byte)(random.Next(2) == 0 ? Instruction.SHL : Instruction.SHR));
                    break;
                case 14:
                    // Wide constant pair feeding an operator: the fold path through the constant pool.
                    random.NextBytes(wide);
                    code.Add((byte)Instruction.PUSH32);
                    code.AddRange(wide);
                    random.NextBytes(wide);
                    code.Add((byte)Instruction.PUSH32);
                    code.AddRange(wide);
                    code.Add((byte)(random.Next(3) switch { 0 => Instruction.ADD, 1 => Instruction.MUL, _ => Instruction.AND }));
                    break;
                case 15:
                    // Unconditional static jump to the very next instruction: the fused StaticJump
                    // shape, and a jump arrival on a marker whose gas the analyzer may have elided.
                    int target = code.Count + 4;
                    code.AddRange([(byte)Instruction.PUSH2, (byte)(target >> 8), (byte)target, (byte)Instruction.JUMP, (byte)Instruction.JUMPDEST]);
                    jumpDests.Add(target);
                    break;
                case 16:
                    // Boundary ops that keep a block open - the widest behavioural change.
                    switch (random.Next(5))
                    {
                        case 0: code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.MLOAD]); break;
                        case 1: code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.MSTORE8]); break;
                        case 2: code.AddRange([(byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x00, (byte)Instruction.KECCAK256]); break;
                        case 3: code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.CALLDATALOAD]); break;
                        default: code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(8), (byte)Instruction.SLOAD]); break;
                    }

                    break;
                case 17:
                    // A table handler that consumes its successor and lands past it - adjacent to a
                    // random JUMPDEST from case 0 this is the elided-marker landing.
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.EXTCODESIZE, (byte)Instruction.ISZERO]);
                    break;
                case 18:
                    // Pushes of every width, so folds and pool references cross the PUSH8/PUSH9 seam.
                    int width = 3 + random.Next(30);
                    code.Add((byte)((byte)Instruction.PUSH1 + width - 1));
                    for (int k = 0; k < width; k++) code.Add((byte)random.Next(256));
                    break;
                default:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.POP]);
                    break;
            }
        }

        // Return the top of the stack so a wrong value is observable; an empty stack underflows
        // identically on both interpreters, which is a comparison too.
        code.AddRange([
            (byte)Instruction.PUSH1, 0x00, (byte)Instruction.MSTORE,
            (byte)Instruction.PUSH1, 0x20, (byte)Instruction.PUSH1, 0x00, (byte)Instruction.RETURN]);
        return code.ToArray();
    }

    private ExecutionCapture RunFor(byte[] code, bool useStream, ulong gasLimit)
    {
        Setup();
        bool enabledBefore = StreamInterpreter.Enabled;
        int thresholdBefore = StreamInterpreter.BuildThreshold;
        bool forceBefore = StreamInterpreter.ForceAllContexts;
        StreamInterpreter.Enabled = useStream;
        StreamInterpreter.ForceAllContexts = useStream;
        try
        {
            (Block block, Transaction transaction) = PrepareTx(Activation, gasLimit, code);

            if (useStream)
            {
                StreamInterpreter.BuildThreshold = 1;
                CodeInfo codeInfo = CodeInfoRepository.GetCachedCodeInfo(Recipient, Spec);
                if (!System.Threading.SpinWait.SpinUntil(() => codeInfo.GetOrBuildStream() is not null, TimeSpan.FromSeconds(5)))
                    Assert.Fail("the stream did not build within the timeout");
            }

            ExecutionCapture tracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            return tracer;
        }
        finally
        {
            StreamInterpreter.Enabled = enabledBefore;
            StreamInterpreter.BuildThreshold = thresholdBefore;
            StreamInterpreter.ForceAllContexts = forceBefore;
        }
    }

    private sealed class ExecutionCapture : TxTracer
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
