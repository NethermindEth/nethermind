// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Numerics;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

// MULMOD repeated in one prime field, as MiMC, Poseidon and elliptic-curve contracts do: the full-width reduction
// with a repeated modulus (cached per thread by Int256), checked against BigInteger through the interpreter.
[Parallelizable(ParallelScope.Self)]
public class MulModFieldTests : VirtualMachineTestsBase
{
    private static readonly BigInteger Bn254R = BigInteger.Parse("21888242871839275222246405745257275088548364400416085262225403389211969241921");
    private static readonly BigInteger Bn254P = BigInteger.Parse("21888242871839275222246405745257275088696311157297823662689037894645226208583");
    private static readonly BigInteger Secp256k1P = BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007908834671663");
    private static readonly BigInteger Max = (BigInteger.One << 256) - 1;

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (bool traced in new[] { false, true })
        {
            yield return new TestCaseData(new[] { Bn254R }, Bn254R - 12345, traced).SetArgDisplayNames("bn254r", traced ? "traced" : "untraced");
            yield return new TestCaseData(new[] { Secp256k1P }, Secp256k1P - 1, traced).SetArgDisplayNames("secp256k1p", traced ? "traced" : "untraced");
            yield return new TestCaseData(new[] { Max - 2 }, Max - 7, traced).SetArgDisplayNames("max-2", traced ? "traced" : "untraced");
            yield return new TestCaseData(new[] { Bn254R, Bn254P }, Bn254R - 3, traced).SetArgDisplayNames("alternating", traced ? "traced" : "untraced");
        }
    }

    // x <- x * x mod p, then x <- x * k mod p, round after round; the moduli rotate when several are given.
    [TestCaseSource(nameof(Cases))]
    public void Repeated_mulmod_matches_BigInteger(BigInteger[] moduli, BigInteger start, bool traced)
    {
        const int rounds = 48;
        BigInteger k = Max - 1;
        List<byte> code = [(byte)Instruction.PUSH32, .. ((UInt256)start).ToBigEndian()];
        BigInteger x = start;
        for (int i = 0; i < rounds; i++)
        {
            BigInteger p = moduli[i % moduli.Length];
            // [x] -> [x, p, x, x] -> [x, x*x mod p] -> [x*x mod p]
            code.AddRange([(byte)Instruction.PUSH32, .. ((UInt256)p).ToBigEndian(), (byte)Instruction.DUP2, (byte)Instruction.DUP3,
                (byte)Instruction.MULMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
            x = x * x % p;
            // [x] -> [x, p, k, x] -> [x, x*k mod p] -> [x*k mod p]
            code.AddRange([(byte)Instruction.PUSH32, .. ((UInt256)p).ToBigEndian(), (byte)Instruction.PUSH32, .. ((UInt256)k).ToBigEndian(),
                (byte)Instruction.DUP3, (byte)Instruction.MULMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
            x = x * k % p;
        }

        code.AddRange([(byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE, (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 0,
            (byte)Instruction.RETURN]);
        TestAllTracerWithOutput tracer = traced ? new TestAllTracerWithOutput() : new UntracedTracer();

        Execute(tracer, [.. code]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            Assert.That(tracer.ReturnValue, Is.EqualTo(((UInt256)x).ToBigEndian()));
        }
    }

    private sealed class UntracedTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }
}
