// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// SHL, SHR and SUB through the interpreter with a tracer that does not trace instructions, so dispatch runs the
/// untraced handlers a node runs (the VM tests use TestAllTracerWithOutput, which selects the traced ones).
/// </summary>
public class UntracedShiftSubtractTests : VirtualMachineTestsBase
{
    // SHL and SHR arrive with Constantinople (EIP-145).
    protected override ulong BlockNumber => 1;

    protected override ISpecProvider SpecProvider => new CustomSpecProvider(
        ((ForkActivation)0, Byzantium.Instance), ((ForkActivation)1, Constantinople.Instance));

    private static readonly BigInteger Mask = (BigInteger.One << 256) - 1;
    // About 15 gas and one memory word per case: 400 cases stay well inside the base's 100,000 gas limit.
    private const int CasesPerTransaction = 400;

    private sealed class OutputTracer : TxTracer
    {
        public override bool IsTracingReceipt => true;
        public byte[]? ReturnValue { get; private set; }
        public bool Failed { get; private set; }
        public string? Error { get; private set; }

        public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
            => ReturnValue = output;

        public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
        {
            Failed = true;
            Error = error;
        }
    }

    private static IEnumerable<UInt256> Values()
    {
        yield return UInt256.Zero;
        yield return UInt256.One;
        yield return UInt256.MaxValue;
        yield return new UInt256(0, 0, 0, 1UL << 63);
        yield return new UInt256(ulong.MaxValue, 0, 0, 0);
        yield return new UInt256(0, ulong.MaxValue, 0, 0);
        yield return new UInt256(0, 0, ulong.MaxValue, 0);
        yield return new UInt256(0, 0, 0, ulong.MaxValue);
        yield return new UInt256(0x0123456789abcdefUL, 0xfedcba9876543210UL, 0x0f1e2d3c4b5a6978UL, 0x8796a5b4c3d2e1f0UL);
        Random random = new(0xE0E0);
        byte[] bytes = new byte[32];
        for (int i = 0; i < 12; i++)
        {
            random.NextBytes(bytes);
            yield return new UInt256(bytes, isBigEndian: true);
        }
    }

    private static IEnumerable<UInt256> Amounts()
    {
        for (ulong n = 0; n <= 300; n++) yield return n;
        // Amounts of 256 or more that do not fit a limb, or have a small low limb under high limbs.
        yield return new UInt256(0, 1, 0, 0);
        yield return new UInt256(1, 1, 0, 0);
        yield return new UInt256(64, 0, 1, 0);
        yield return new UInt256(255, 0, 0, 1);
        yield return new UInt256(0, 0, 0, 1UL << 63);
        yield return UInt256.MaxValue;
        yield return new UInt256(ulong.MaxValue);
        yield return new UInt256((ulong)int.MaxValue + 1);
        yield return new UInt256(uint.MaxValue);
    }

    [Test]
    public void Untraced_shifts_match_BigInteger()
    {
        List<(Instruction Op, UInt256 First, UInt256 Second, UInt256 Expected)> cases = [];
        foreach (UInt256 value in Values())
        {
            foreach (UInt256 amount in Amounts())
            {
                BigInteger x = (BigInteger)value;
                bool big = !amount.IsUint64 || amount.u0 >= 256;
                int n = big ? 0 : (int)amount.u0;
                cases.Add((Instruction.SHL, amount, value, big ? UInt256.Zero : (UInt256)((x << n) & Mask)));
                cases.Add((Instruction.SHR, amount, value, big ? UInt256.Zero : (UInt256)(x >> n)));
            }
        }

        Run(cases);
    }

    [Test]
    public void Untraced_subtract_matches_BigInteger()
    {
        ulong[] limbs = [0, 1, 1UL << 63, ulong.MaxValue];
        List<UInt256> operands = [];
        foreach (ulong u0 in limbs)
            foreach (ulong u1 in limbs)
                foreach (ulong u2 in limbs)
                    foreach (ulong u3 in limbs)
                        operands.Add(new UInt256(u0, u1, u2, u3));
        operands.AddRange(Values());

        List<(Instruction Op, UInt256 First, UInt256 Second, UInt256 Expected)> cases = [];
        for (int i = 0; i < operands.Count; i++)
        {
            // Every operand against a spread of others: borrows generated, propagated and wrapped in every limb.
            for (int j = 0; j < operands.Count; j += 7)
            {
                UInt256 a = operands[i], b = operands[(i + j) % operands.Count];
                cases.Add((Instruction.SUB, a, b, (UInt256)(((BigInteger)a - (BigInteger)b) & Mask)));
            }
        }

        Run(cases);
    }

    private void Run(List<(Instruction Op, UInt256 First, UInt256 Second, UInt256 Expected)> cases)
    {
        for (int start = 0; start < cases.Count; start += CasesPerTransaction)
        {
            int count = Math.Min(CasesPerTransaction, cases.Count - start);
            List<byte> code = [];
            for (int k = 0; k < count; k++)
            {
                (Instruction op, UInt256 first, UInt256 second, _) = cases[start + k];
                // The first operand is the top of the stack, so it is pushed last.
                Push32(code, second);
                Push32(code, first);
                code.Add((byte)op);
                code.Add((byte)Instruction.PUSH2);
                code.Add((byte)((k * 32) >> 8));
                code.Add((byte)(k * 32));
                code.Add((byte)Instruction.MSTORE);
            }

            code.Add((byte)Instruction.PUSH2);
            code.Add((byte)((count * 32) >> 8));
            code.Add((byte)(count * 32));
            code.Add((byte)Instruction.PUSH1);
            code.Add(0);
            code.Add((byte)Instruction.RETURN);

            OutputTracer tracer = Execute(new OutputTracer(), code.ToArray());
            Assert.That(tracer.Failed, Is.False, $"batch at {start} failed: {tracer.Error}");
            byte[] output = tracer.ReturnValue!;
            for (int k = 0; k < count; k++)
            {
                (Instruction op, UInt256 first, UInt256 second, UInt256 expected) = cases[start + k];
                UInt256 actual = new(output.AsSpan(k * 32, 32), isBigEndian: true);
                if (actual != expected)
                    Assert.Fail($"{op} first=0x{first.ToString("X")} second=0x{second.ToString("X")} gave 0x{actual.ToString("X")}, expected 0x{expected.ToString("X")}");
            }
        }
    }

    private static void Push32(List<byte> code, in UInt256 value)
    {
        code.Add((byte)Instruction.PUSH32);
        code.AddRange(value.ToBigEndian());
    }
}
