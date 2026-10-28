// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Crypto;

/// <summary>The BN254, BLS12-381 and KZG operations of <see cref="Accelerators"/>, with the same contracts.</summary>
/// <remarks>
/// On ZisK these call ZisK's own hand-written library routines through call thunks in the runtime, in the same C ABI
/// as the <see cref="Accelerators"/> imports. They spend far fewer steps than the crate's Rust routines, which copy
/// every field element into and out of each precompile call. Elsewhere they forward to <see cref="Accelerators"/>.
/// Callers may pass unpinned spans: no GC safepoint can occur in a call, as the imports suppress the GC transition
/// and the guest is single-threaded.
/// </remarks>
public static unsafe class CurveAccelerators
{
    public static Accelerators.Status BN254G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.BN254G1Add(p1, p2, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 64, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 64, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));
        fixed (byte* a = p1, b = p2, r = result) return ZiskBN254G1Add(a, b, r);
    }

    public static Accelerators.Status BN254G1Mul(ReadOnlySpan<byte> point, ReadOnlySpan<byte> scalar, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.BN254G1Mul(point, scalar, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(point.Length, 64, nameof(point));
        ArgumentOutOfRangeException.ThrowIfNotEqual(scalar.Length, 32, nameof(scalar));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 64, nameof(result));
        fixed (byte* p = point, k = scalar, r = result) return ZiskBN254G1Mul(p, k, r);
    }

    public static Accelerators.Status BN254Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        if (!ZiskFlag.IsActive) return Accelerators.BN254Pairing(pairs, numPairs, out verified);

        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 192 * numPairs, nameof(pairs));
        bool ok;
        Accelerators.Status status;
        fixed (byte* p = pairs) status = ZiskBN254Pairing(p, numPairs, &ok);
        verified = ok;
        return status;
    }

    /// <exception cref="System.Security.Cryptography.CryptographicException">Operation failed.</exception>
    public static bool KzgPointEval(ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> z, ReadOnlySpan<byte> y, ReadOnlySpan<byte> proof)
    {
        if (!ZiskFlag.IsActive) return Accelerators.KzgPointEval(commitment, z, y, proof);

        ArgumentOutOfRangeException.ThrowIfNotEqual(commitment.Length, 48, nameof(commitment));
        ArgumentOutOfRangeException.ThrowIfNotEqual(z.Length, 32, nameof(z));
        ArgumentOutOfRangeException.ThrowIfNotEqual(y.Length, 32, nameof(y));
        ArgumentOutOfRangeException.ThrowIfNotEqual(proof.Length, 48, nameof(proof));
        bool verified;
        Accelerators.Status status;
        fixed (byte* c = commitment, pz = z, py = y, p = proof) status = ZiskKzgPointEval(c, pz, py, p, &verified);
        if (status != Accelerators.Status.OK)
            throw new System.Security.Cryptography.CryptographicException($"{nameof(KzgPointEval)} failed. Status: {status}");
        return verified;
    }

    public static Accelerators.Status Bls12381G1Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381G1Add(p1, p2, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 96, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 96, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));
        fixed (byte* a = p1, b = p2, r = result) return ZiskBls12G1Add(a, b, r);
    }

    public static Accelerators.Status Bls12381G1Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381G1Msm(pairs, numPairs, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 128 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));
        fixed (byte* p = pairs, r = result) return ZiskBls12G1Msm(p, numPairs, r);
    }

    public static Accelerators.Status Bls12381G2Add(ReadOnlySpan<byte> p1, ReadOnlySpan<byte> p2, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381G2Add(p1, p2, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(p1.Length, 192, nameof(p1));
        ArgumentOutOfRangeException.ThrowIfNotEqual(p2.Length, 192, nameof(p2));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));
        fixed (byte* a = p1, b = p2, r = result) return ZiskBls12G2Add(a, b, r);
    }

    public static Accelerators.Status Bls12381G2Msm(ReadOnlySpan<byte> pairs, nuint numPairs, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381G2Msm(pairs, numPairs, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 224 * numPairs, nameof(pairs));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));
        fixed (byte* p = pairs, r = result) return ZiskBls12G2Msm(p, numPairs, r);
    }

    public static Accelerators.Status Bls12381Pairing(ReadOnlySpan<byte> pairs, nuint numPairs, out bool verified)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381Pairing(pairs, numPairs, out verified);

        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)pairs.Length, 288 * numPairs, nameof(pairs));
        bool ok;
        Accelerators.Status status;
        fixed (byte* p = pairs) status = ZiskBls12Pairing(p, numPairs, &ok);
        verified = ok;
        return status;
    }

    public static Accelerators.Status Bls12381MapFpToG1(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381MapFpToG1(fieldElement, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 48, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 96, nameof(result));
        fixed (byte* f = fieldElement, r = result) return ZiskBls12MapFpToG1(f, r);
    }

    public static Accelerators.Status Bls12381MapFp2ToG2(ReadOnlySpan<byte> fieldElement, Span<byte> result)
    {
        if (!ZiskFlag.IsActive) return Accelerators.Bls12381MapFp2ToG2(fieldElement, result);

        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldElement.Length, 96, nameof(fieldElement));
        ArgumentOutOfRangeException.ThrowIfNotEqual(result.Length, 192, nameof(result));
        fixed (byte* f = fieldElement, r = result) return ZiskBls12MapFp2ToG2(f, r);
    }

    [DllImport("__Internal", EntryPoint = "zisklib_bn254_g1_add", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBN254G1Add(byte* p1, byte* p2, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bn254_g1_mul", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBN254G1Mul(byte* point, byte* scalar, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bn254_pairing", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBN254Pairing(byte* pairs, nuint numPairs, bool* verified);

    [DllImport("__Internal", EntryPoint = "zisklib_kzg_point_eval", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskKzgPointEval(byte* commitment, byte* z, byte* y, byte* proof, bool* verified);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_g1_add", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12G1Add(byte* p1, byte* p2, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_g1_msm", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12G1Msm(byte* pairs, nuint numPairs, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_g2_add", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12G2Add(byte* p1, byte* p2, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_g2_msm", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12G2Msm(byte* pairs, nuint numPairs, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_pairing", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12Pairing(byte* pairs, nuint numPairs, bool* verified);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_map_fp_to_g1", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12MapFpToG1(byte* fieldElement, byte* result);

    [DllImport("__Internal", EntryPoint = "zisklib_bls12_map_fp2_to_g2", ExactSpelling = true), SuppressGCTransition]
    private static extern Accelerators.Status ZiskBls12MapFp2ToG2(byte* fieldElement, byte* result);
}
