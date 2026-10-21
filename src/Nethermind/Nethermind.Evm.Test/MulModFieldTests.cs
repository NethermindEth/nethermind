// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

// MULMOD repeated in one prime field, as MiMC, Poseidon and elliptic-curve contracts do. Int256 reduces products of at
// least 2^384 modulo BN254 r and p, secp256k1 p and n, and P-256 p and n by Barrett reduction, and other moduli by
// division; these check both through the interpreter against BigInteger.
[Parallelizable(ParallelScope.Self)]
public class MulModFieldTests : VirtualMachineTestsBase
{
    private static readonly BigInteger Bn254R = Hex("30644e72e131a029b85045b68181585d2833e84879b9709143e1f593f0000001");
    private static readonly BigInteger Bn254P = Hex("30644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd47");
    private static readonly BigInteger Secp256k1P = Hex("fffffffffffffffffffffffffffffffffffffffffffffffffffffffefffffc2f");
    private static readonly BigInteger Max = (BigInteger.One << 256) - 1;

    private static IEnumerable<TestCaseData> Cases()
    {
        foreach (bool traced in new[] { false, true })
        {
            string mode = traced ? "traced" : "untraced";
            yield return new TestCaseData(new[] { Bn254R }, Bn254R - 12345, traced).SetArgDisplayNames("bn254r", mode);
            yield return new TestCaseData(new[] { Secp256k1P }, Secp256k1P - 1, traced).SetArgDisplayNames("secp256k1p", mode);
            yield return new TestCaseData(new[] { Max - 2 }, Max - 7, traced).SetArgDisplayNames("max-2", mode);
            yield return new TestCaseData(new[] { Bn254R, Bn254P }, Bn254R - 3, traced).SetArgDisplayNames("alternating", mode);
        }
    }

    [Test]
    public void Field_constants_are_the_curve_moduli()
    {
        Assert.That(Bn254R.ToString(), Is.EqualTo("21888242871839275222246405745257275088548364400416034343698204186575808495617"));
        Assert.That(Bn254P.ToString(), Is.EqualTo("21888242871839275222246405745257275088696311157297823662689037894645226208583"));
        Assert.That(Secp256k1P, Is.EqualTo((BigInteger.One << 256) - (BigInteger.One << 32) - 977));
    }

    // x <- x * x mod p, then x <- x * k mod p, round after round; the moduli rotate when several are given.
    [TestCaseSource(nameof(Cases))]
    public void Repeated_mulmod_matches_BigInteger(BigInteger[] moduli, BigInteger start, bool traced)
    {
        const int rounds = 48;
        BigInteger k = Max - 1;
        List<byte> code = [];
        Push(code, start);
        BigInteger x = start;
        for (int i = 0; i < rounds; i++)
        {
            BigInteger p = moduli[i % moduli.Length];
            // [x] -> [x, p, x, x] -> [x, x*x mod p] -> [x*x mod p]
            Push(code, p);
            code.AddRange([(byte)Instruction.DUP2, (byte)Instruction.DUP3, (byte)Instruction.MULMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
            x = x * x % p;
            // [x] -> [x, p, k, x] -> [x, x*k mod p] -> [x*k mod p]
            Push(code, p);
            Push(code, k);
            code.AddRange([(byte)Instruction.DUP3, (byte)Instruction.MULMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
            x = x * k % p;
        }

        AssertReturns(code, x, traced);
    }

    // Tornado Cash's MerkleTreeWithHistory.hashLeftRight over a MiMC-style Feistel sponge mod BN254 r: per round
    // t = xL + c (ADDMOD), t^2, t^4 = (t^2)^2, t^5 = t^4 * t (three MULMOD, two of them squares), xL <- xR + t^5,
    // xR <- xL. The round constants are arbitrary field elements; the operand shapes are the hasher's.
    [TestCase(false)]
    [TestCase(true)]
    public void Mimc_sponge_hash_left_right_matches_BigInteger(bool traced)
    {
        const int rounds = 40;
        BigInteger r = Bn254R;
        BigInteger[] constants = new BigInteger[rounds];
        for (int i = 0; i < rounds; i++) constants[i] = (BigInteger.Pow(7, i + 5) + 31 * i) % r;
        BigInteger left = Hex("2fe54c60d3acabf3343a35b6eba15db4821b340f76e741e2249685ed4899af6c");
        BigInteger right = Hex("1a332ca2cd2436bdc6796e6e4244ebf6f7e359868b7252e55342f766e4088082");

        List<byte> code = [];
        // [xR = C = 0, xL = R = left]
        Push(code, BigInteger.Zero);
        Push(code, left);
        Sponge(code, constants, r);
        // R = R + right mod r: [C, R] -> [C, R, r, right, R] -> [C, R, R + right] -> [C, R + right]
        Push(code, r);
        Push(code, right);
        code.AddRange([(byte)Instruction.DUP3, (byte)Instruction.ADDMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
        Sponge(code, constants, r);

        (BigInteger xl, BigInteger xr) = SpongeModel(left, BigInteger.Zero, constants, r);
        (xl, _) = SpongeModel((xl + right) % r, xr, constants, r);

        AssertReturns(code, xl, traced);
    }

    private static void Sponge(List<byte> code, BigInteger[] constants, BigInteger r)
    {
        foreach (BigInteger c in constants)
        {
            // [xR, xL] -> [xR, xL, r, c, xL] -> [xR, xL, t]
            Push(code, r);
            Push(code, c);
            code.AddRange([(byte)Instruction.DUP3, (byte)Instruction.ADDMOD]);
            // [xR, xL, t] -> [xR, xL, t, r, t, t] -> [xR, xL, t, t^2]
            Push(code, r);
            code.AddRange([(byte)Instruction.DUP2, (byte)Instruction.DUP3, (byte)Instruction.MULMOD]);
            // [xR, xL, t, t^2] -> [xR, xL, t, t^2, t^4] -> [xR, xL, t, t^4]
            Push(code, r);
            code.AddRange([(byte)Instruction.DUP2, (byte)Instruction.DUP3, (byte)Instruction.MULMOD, (byte)Instruction.SWAP1, (byte)Instruction.POP]);
            // [xR, xL, t, t^4] -> [xR, xL, t, t^4, r, t, t^4] -> [xR, xL, t, t^4, t^5] -> [xR, xL, t^5]
            Push(code, r);
            code.AddRange([(byte)Instruction.DUP3, (byte)Instruction.DUP3, (byte)Instruction.MULMOD,
                (byte)Instruction.SWAP2, (byte)Instruction.POP, (byte)Instruction.POP]);
            // [xR, xL, t^5] -> [xR, xL, r, t^5, xR] -> [xR, xL, xR + t^5] -> [xL, xR + t^5]
            Push(code, r);
            code.AddRange([(byte)Instruction.SWAP1, (byte)Instruction.DUP4, (byte)Instruction.ADDMOD,
                (byte)Instruction.SWAP2, (byte)Instruction.POP, (byte)Instruction.SWAP1]);
        }
    }

    private static (BigInteger Left, BigInteger Right) SpongeModel(BigInteger xl, BigInteger xr, BigInteger[] constants, BigInteger r)
    {
        foreach (BigInteger c in constants)
        {
            BigInteger t = (xl + c) % r;
            BigInteger t2 = t * t % r, t4 = t2 * t2 % r, t5 = t4 * t % r;
            (xl, xr) = ((xr + t5) % r, xl);
        }

        return (xl, xr);
    }

    private void AssertReturns(List<byte> code, BigInteger expected, bool traced)
    {
        code.AddRange([(byte)Instruction.PUSH1, 0, (byte)Instruction.MSTORE, (byte)Instruction.PUSH1, 32, (byte)Instruction.PUSH1, 0,
            (byte)Instruction.RETURN]);
        TestAllTracerWithOutput tracer = traced ? new TestAllTracerWithOutput() : new UntracedTracer();

        Execute(tracer, [.. code]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success), tracer.Error);
            Assert.That(tracer.ReturnValue, Is.EqualTo(((UInt256)expected).ToBigEndian()));
        }
    }

    private static void Push(List<byte> code, BigInteger value) => code.AddRange([(byte)Instruction.PUSH32, .. ((UInt256)value).ToBigEndian()]);

    private static BigInteger Hex(string value) => BigInteger.Parse("0" + value, NumberStyles.HexNumber);

    private sealed class UntracedTracer : TestAllTracerWithOutput
    {
        public override bool IsTracingInstructions => false;
    }
}
