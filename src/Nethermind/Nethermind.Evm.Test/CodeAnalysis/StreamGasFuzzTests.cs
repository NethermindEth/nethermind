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
/// Randomized gas differential over the opcode alphabet the block analyzer treats specially:
/// jump targets, static and dynamic jumps, the fused glue pairs and the boundary ops that keep a
/// block open. The hand-written differential cases cover the shapes we thought of; a cross-client
/// gas mismatch showed that the shapes we did not think of are the ones that break, so this walks
/// the space instead. Every generated program runs on both interpreters and the gas must match
/// exactly - a seed that fails prints its bytecode, which is the reproduction.
/// </summary>
[TestFixture]
public class StreamGasFuzzTests : VirtualMachineTestsBase
{
    protected override ulong BlockNumber => MainnetSpecProvider.ParisBlockNumber + 4;
    protected override ulong Timestamp => MainnetSpecProvider.OsakaBlockTimestamp;
    protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

    private const int Programs = 400;

    [Test]
    public void StreamGas_MatchesByteCodeLoop_OverRandomJumpHeavyPrograms()
    {
        List<string> failures = [];
        for (int seed = 0; seed < Programs; seed++)
        {
            byte[] code = Generate(seed);

            Setup();
            ulong baseline = RunFor(code, useStream: false);
            Setup();
            ulong streamed = RunFor(code, useStream: true);

            if (baseline != streamed)
            {
                failures.Add($"seed {seed}: baseline {baseline}, streamed {streamed}, delta {(long)streamed - (long)baseline}, code 0x{Convert.ToHexString(code)}");
                if (failures.Count == 5) break;
            }
        }

        if (failures.Count > 0)
        {
            StringBuilder message = new($"{failures.Count} program(s) charged different gas on the two interpreters:");
            foreach (string failure in failures)
            {
                message.AppendLine().Append("  ").Append(failure);
            }

            Assert.Fail(message.ToString());
        }
    }

    /// <summary>
    /// Builds a program whose jump targets are always real JUMPDESTs, so the run exercises jump
    /// accounting rather than invalid-destination failures, and whose counters are bounded so it
    /// terminates. Weighted toward the constructs the analyzer rewrites.
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

        int slots = 6 + random.Next(18);
        for (int i = 0; i < slots; i++)
        {
            switch (random.Next(12))
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
                default:
                    code.AddRange([(byte)Instruction.PUSH1, (byte)random.Next(64), (byte)Instruction.POP]);
                    break;
            }
        }

        code.Add((byte)Instruction.STOP);
        return code.ToArray();
    }

    private ulong RunFor(byte[] code, bool useStream)
    {
        bool enabledBefore = StreamInterpreter.Enabled;
        int thresholdBefore = StreamInterpreter.BuildThreshold;
        bool forceBefore = StreamInterpreter.ForceAllContexts;
        StreamInterpreter.Enabled = useStream;
        StreamInterpreter.ForceAllContexts = useStream;
        try
        {
            (Block block, Transaction transaction) = PrepareTx(Activation, 2_000_000, code);

            if (useStream)
            {
                StreamInterpreter.BuildThreshold = 1;
                CodeInfo codeInfo = CodeInfoRepository.GetCachedCodeInfo(Recipient, Spec);
                if (!System.Threading.SpinWait.SpinUntil(() => codeInfo.GetOrBuildStream() is not null, TimeSpan.FromSeconds(5)))
                    Assert.Fail("the stream did not build within the timeout");
            }

            GasCaptureTracer tracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, SpecProvider.GetSpec(block.Header)), tracer);
            return tracer.GasSpent;
        }
        finally
        {
            StreamInterpreter.Enabled = enabledBefore;
            StreamInterpreter.BuildThreshold = thresholdBefore;
            StreamInterpreter.ForceAllContexts = forceBefore;
        }
    }

    private sealed class GasCaptureTracer : TxTracer
    {
        public ulong GasSpent { get; private set; }

        public override bool IsTracingReceipt => true;

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
            GasSpent = gasSpent.SpentGas;

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null) =>
            GasSpent = gasSpent.SpentGas;
    }
}
