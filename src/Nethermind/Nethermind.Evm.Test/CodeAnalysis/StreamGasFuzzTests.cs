// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
/// Randomized differential between the stream interpreter and the bytecode loop. Each program runs
/// on both twice - ample gas, then a budget that starves it mid-run - and gas, status and output
/// must match exactly. A failing seed prints its bytecode as the reproduction.
/// </summary>
// Mutates process-wide StreamInterpreter statics, so it must not run alongside other EVM tests.
[TestFixture, NonParallelizable]
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
        int completed = 0;
        int starved = 0;
        for (int seed = 0; seed < Programs; seed++)
        {
            byte[] code = Generate(seed);

            ExecutionCapture baseline = RunFor(code, useStream: false, AmpleGas);
            ExecutionCapture streamed = RunFor(code, useStream: true, AmpleGas);
            Compare(failures, seed, "ample", code, baseline, streamed);
            if (baseline.StatusCode == Evm.StatusCode.Success) completed++;

            if (baseline.GasSpent > GasCostOf.Transaction)
            {
                ulong execGas = baseline.GasSpent - GasCostOf.Transaction;
                ulong budget = GasCostOf.Transaction + execGas * (ulong)(1 + seed % 96) / 97;
                ExecutionCapture tightBaseline = RunFor(code, useStream: false, budget);
                ExecutionCapture tightStreamed = RunFor(code, useStream: true, budget);
                Compare(failures, seed, $"tight budget {budget}", code, tightBaseline, tightStreamed);
                if (tightBaseline.StatusCode == Evm.StatusCode.Failure) starved++;
            }

            if (failures.Count >= 5)
                break;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(completed, Is.GreaterThan(Programs / 2),
                "most generated programs must run to completion, or the comparison is between two immediate halts");
            Assert.That(starved, Is.GreaterThan(Programs / 4),
                "the tight-budget pass must actually run out of gas, or the metered fallback is never crossed");
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
    /// Builds a program weighted toward the constructs the analyzer rewrites. Jump targets are
    /// always real JUMPDESTs and conditional branches decrement the word they test, so a backward
    /// jump makes progress towards leaving the loop instead of spinning until the gas runs out.
    /// </summary>
    private static byte[] Generate(int seed)
    {
        Random random = new(seed);
        List<byte> code = [];
        List<int> jumpDests = [];

        // A leading counter for loops to decrement, and a dozen words under it so the deep DUP and
        // SWAP forms have something to reach.
        code.AddRange([(byte)Instruction.PUSH1, (byte)(1 + random.Next(3))]);
        for (int i = 0; i < 12; i++)
        {
            code.AddRange([(byte)Instruction.PUSH1, (byte)(0x40 + i)]);
        }

        byte[] wide = new byte[32];
        int slots = 6 + random.Next(18);
        for (int i = 0; i < slots; i++)
        {
            switch (random.Next(24))
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
                    int condDest = jumpDests[random.Next(jumpDests.Count)];
                    code.AddRange([
                        (byte)Instruction.PUSH1, 0x01, (byte)Instruction.SWAP1, (byte)Instruction.SUB,
                        (byte)Instruction.DUP1,
                        (byte)Instruction.PUSH2, (byte)(condDest >> 8), (byte)condDest, (byte)Instruction.JUMPI]);
                    break;
                case 10 when jumpDests.Count > 0:
                    // Dynamic conditional jump: the SWAP1 stops the static-jump fusion from
                    // claiming the pair, which is the point of this arm.
                    int dynDest = jumpDests[random.Next(jumpDests.Count)];
                    code.AddRange([
                        (byte)Instruction.PUSH2, (byte)(dynDest >> 8), (byte)dynDest,
                        (byte)Instruction.PUSH1, (byte)random.Next(2),
                        (byte)Instruction.SWAP1, (byte)Instruction.JUMPI]);
                    break;
                case 11:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.AND]);
                    if (random.Next(2) == 0) code.Add((byte)Instruction.ISZERO);
                    break;
                case 12:
                    code.AddRange([
                        (byte)Instruction.PUSH1, (byte)(random.Next(4) == 0 ? 0 : random.Next(256)),
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)(random.Next(2) == 0 ? Instruction.DIV : Instruction.MOD)]);
                    break;
                case 13:
                    // Sometimes past 255, so saturation folds and cores must agree.
                    if (random.Next(3) == 0)
                        code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH2, 0x01, 0x00]);
                    else
                        code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.PUSH1, (byte)random.Next(256)]);
                    code.Add((byte)(random.Next(2) == 0 ? Instruction.SHL : Instruction.SHR));
                    break;
                case 14:
                    random.NextBytes(wide);
                    code.Add((byte)Instruction.PUSH32);
                    code.AddRange(wide);
                    random.NextBytes(wide);
                    code.Add((byte)Instruction.PUSH32);
                    code.AddRange(wide);
                    code.Add((byte)(random.Next(3) switch { 0 => Instruction.ADD, 1 => Instruction.MUL, _ => Instruction.AND }));
                    break;
                case 15:
                    // Fused StaticJump, arriving on a marker whose gas the analyzer may have elided.
                    int target = code.Count + 4;
                    code.AddRange([(byte)Instruction.PUSH2, (byte)(target >> 8), (byte)target, (byte)Instruction.JUMP, (byte)Instruction.JUMPDEST]);
                    jumpDests.Add(target);
                    break;
                case 16:
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
                    // A handler that consumes its successor and lands past it; next to a JUMPDEST
                    // from case 0 this is the elided-marker landing.
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)Instruction.EXTCODESIZE, (byte)Instruction.ISZERO]);
                    break;
                case 22:
                    code.AddRange([
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)Instruction.PUSH1, (byte)(random.Next(4) == 0 ? 0xFF : random.Next(40)),
                        (byte)Instruction.SHL, (byte)Instruction.SUB]);
                    break;
                case 21:
                    code.AddRange([
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)Instruction.SUB, (byte)Instruction.AND]);
                    break;
                case 19:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(256), (byte)((byte)Instruction.DUP1 + random.Next(8))]);
                    break;
                case 20:
                    code.AddRange([
                        (byte)Instruction.PUSH1, (byte)random.Next(256),
                        (byte)Instruction.PUSH1, (byte)random.Next(9),
                        (byte)(random.Next(4) switch { 0 => Instruction.SHL, 1 => Instruction.SHR, 2 => Instruction.ADD, _ => Instruction.DIV })]);
                    break;
                case 18:
                    // Every push width, so folds and pool references cross the PUSH8/PUSH9 seam.
                    int width = 3 + random.Next(30);
                    code.Add((byte)((byte)Instruction.PUSH1 + width - 1));
                    for (int k = 0; k < width; k++) code.Add((byte)random.Next(256));
                    break;
                default:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.POP]);
                    break;
            }
        }

        // Return the top of the stack, so a wrong value is observable even when it never flips a
        // branch or moves gas.
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
                if (!SpinWait.SpinUntil(() => codeInfo.GetOrBuildStream() is not null, TimeSpan.FromSeconds(5)))
                    Assert.Fail($"the stream did not build within the timeout for code 0x{Convert.ToHexString(code)}");
            }

            long framesBefore = StreamInterpreter.FramesExecuted;
            ExecutionCapture tracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), new CancellationTxTracer(tracer));
            if (useStream && StreamInterpreter.FramesExecuted == framesBefore)
                Assert.Fail($"the stream did not engage, so this comparison proved nothing, for code 0x{Convert.ToHexString(code)}");
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
