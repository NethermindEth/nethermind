// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Crypto;

/// <summary>The BN254, BLS12-381 and KZG operations of <see cref="Accelerators"/>, with the same contracts.</summary>
/// <remarks>
/// On ZisK these call ZisK's own hand-written library routines, the <c>Zisk</c> counterparts in <see cref="Accelerators"/>.
/// They spend far fewer steps than the crate's Rust routines, which copy every field element into and out of each
/// precompile call. Elsewhere they forward to the standard accelerators.
/// </remarks>
public static class CurveAccelerators
{
    public static Accelerators.Status BN254G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result) =>
        ZiskFlag.IsActive ? Accelerators.ZiskBN254G1Add(p1, p2, result) : Accelerators.BN254G1Add(p1, p2, result);

    public static Accelerators.Status BN254G1Mul(ReadOnlySpan<byte> point, ReadOnlySpan<byte> scalar, Span<byte> result) =>
        ZiskFlag.IsActive ? Accelerators.ZiskBN254G1Mul(point, scalar, result) : Accelerators.BN254G1Mul(point, scalar, result);

    public static Accelerators.Status BN254Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBN254Pairing(pairs, numPairs, out verified)
            : Accelerators.BN254Pairing(pairs, numPairs, out verified);

    /// <exception cref="System.Security.Cryptography.CryptographicException">Operation failed.</exception>
    public static bool KzgPointEval(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskKzgPointEval(commitment, z, y, proof)
            : Accelerators.KzgPointEval(commitment, z, y, proof);

    public static Accelerators.Status Bls12381G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result) =>
        ZiskFlag.IsActive ? Accelerators.ZiskBls12381G1Add(p1, p2, result) : Accelerators.Bls12381G1Add(p1, p2, result);

    public static Accelerators.Status Bls12381G1Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBls12381G1Msm(pairs, numPairs, result)
            : Accelerators.Bls12381G1Msm(pairs, numPairs, result);

    public static Accelerators.Status Bls12381G2Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result) =>
        ZiskFlag.IsActive ? Accelerators.ZiskBls12381G2Add(p1, p2, result) : Accelerators.Bls12381G2Add(p1, p2, result);

    public static Accelerators.Status Bls12381G2Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBls12381G2Msm(pairs, numPairs, result)
            : Accelerators.Bls12381G2Msm(pairs, numPairs, result);

    public static Accelerators.Status Bls12381Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBls12381Pairing(pairs, numPairs, out verified)
            : Accelerators.Bls12381Pairing(pairs, numPairs, out verified);

    public static Accelerators.Status Bls12381MapFpToG1(ReadOnlySpan<byte> fieldElement, Span<byte> result) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBls12381MapFpToG1(fieldElement, result)
            : Accelerators.Bls12381MapFpToG1(fieldElement, result);

    public static Accelerators.Status Bls12381MapFp2ToG2(ReadOnlySpan<byte> fieldElement, Span<byte> result) =>
        ZiskFlag.IsActive
            ? Accelerators.ZiskBls12381MapFp2ToG2(fieldElement, result)
            : Accelerators.Bls12381MapFp2ToG2(fieldElement, result);
}
