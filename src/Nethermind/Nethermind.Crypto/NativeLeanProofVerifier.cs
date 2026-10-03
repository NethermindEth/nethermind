// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Checks signature claims and public program commitments with the pinned native backend;
/// unavailable libraries fail verification closed, while proving reports an error.</summary>
// Pinned spans remain valid for each call; Rust bounds all buffers and owns returned proof allocations.
public sealed unsafe partial class NativeLeanProofVerifier : ILeanProofVerifier
{
    private const string Library = "nethermind_lean";
    private const uint ExpectedAbiVersion = 4;
    private const int MaxRecursiveInputs = 16;
    private static readonly Lazy<bool> BackendAvailable = new(CheckBackend);
    public const int MaxProofBytes = Eip8288Constants.MaxProofBytes;
    public static readonly NativeLeanProofVerifier Instance = new();

    /// <summary>Checks the ABI and recursive guest key once, before an EIP-8288 node starts processing.</summary>
    public void EnsureAvailable() => _ = BackendAvailable.Value;

    private static bool CheckBackend()
    {
        try
        {
            if (!AggregatedVerificationKey.AsSpan().SequenceEqual(Eip8288Constants.AggregatedVk))
                throw new InvalidOperationException("The native Lean recursive guest key does not match this node.");
            Span<uint> limits = stackalloc uint[9];
            ReadOnlySpan<uint> expectedLimits = [MaxProofBytes, Eip8288Constants.MaxProofDependencies,
                MaxRecursiveInputs, Eip8288Constants.LeanSphincsWitnessBytes,
                Eip8288Constants.MaxGenericStarkProofs, 16384, 65535, Eip8288Constants.MaxAggregationInputBytes, Eip8288Constants.MaxSphincsGuestProofBytes];
            fixed (uint* p = limits)
                if (nlean_limits(p, (nuint)limits.Length) != 1 ||
                    !limits.SequenceEqual(expectedLimits))
                    throw new InvalidOperationException("The native Lean acceptance bounds do not match this node.");
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception) || exception is InvalidOperationException)
        {
            throw new InvalidOperationException("EIP-8288 requires the pinned native Lean backend. Build with BuildLeanFfi=true and install the host library beside Nethermind.", exception);
        }
    }

    /// <summary>Native library ABI version.</summary>
    public static uint AbiVersion => nlean_abi_version();

    /// <summary>The pinned recursive guest's Fiat-Shamir verification key.</summary>
    public static byte[] AggregatedVerificationKey
    {
        get
        {
            byte[] key = new byte[32];
            fixed (byte* p = key)
            {
                if (AbiVersion != ExpectedAbiVersion || nlean_aggregated_vk(p) != 1) throw new InvalidOperationException("Lean verification key unavailable");
            }
            return key;
        }
    }

    /// <inheritdoc/>
    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        if (witness.Length != Eip8288Constants.LeanSphincsWitnessBytes) return false;
        try
        {
            fixed (byte* d = dataHash.Bytes)
            fixed (byte* v = verificationKey.Bytes)
            fixed (byte* w = witness)
                return AbiVersion == ExpectedAbiVersion && nlean_verify_leansphincs(d, v, w, (nuint)witness.Length) == 1;
        }
        catch (Exception e) when (IsUnavailable(e)) { return false; }
    }

    /// <inheritdoc/>
    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        if (witness.IsEmpty || witness.Length > MaxProofBytes - 212) return false;
        try
        {
            fixed (byte* d = dataHash.Bytes)
            fixed (byte* v = verificationKey.Bytes)
            fixed (byte* w = witness)
                return AbiVersion == ExpectedAbiVersion && nlean_verify_leanstark(d, v, w, (nuint)witness.Length) == 1;
        }
        catch (Exception e) when (IsUnavailable(e)) { return false; }
    }

    /// <inheritdoc/>
    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        if (aggregatedVk.Length != 32 || proof.IsEmpty || proof.Length > MaxProofBytes) return false;
        try
        {
            fixed (byte* h = depsHash.Bytes)
            fixed (byte* vk = aggregatedVk)
            fixed (byte* p = proof)
                return AbiVersion == ExpectedAbiVersion && nlean_verify_recursive(h, vk, (nuint)aggregatedVk.Length, p, (nuint)proof.Length) == 1;
        }
        catch (Exception e) when (IsUnavailable(e)) { return false; }
    }

    /// <inheritdoc/>
    public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        try { return Prove(in depsHash, aggregatedVk, input); }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            throw new InvalidOperationException("Native Lean backend unavailable", exception);
        }
    }

    private static byte[] Prove(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        byte[] witness = SerializeInput(input);
        if (aggregatedVk.Length != 32 || AbiVersion != ExpectedAbiVersion) throw new InvalidOperationException("Incompatible Lean verifier ABI or key");
        byte* proof = null;
        nuint length = 0;
        try
        {
            fixed (byte* h = depsHash.Bytes)
            fixed (byte* vk = aggregatedVk)
            fixed (byte* w = witness)
                if (nlean_prove_recursive(h, vk, (nuint)aggregatedVk.Length, w, (nuint)witness.Length, &proof, &length) != 1)
                    throw new InvalidOperationException("Lean aggregation failed");
            if (proof == null || length > MaxProofBytes) throw new InvalidOperationException("Invalid native Lean proof buffer");
            return new ReadOnlySpan<byte>(proof, checked((int)length)).ToArray();
        }
        finally
        {
            if (proof != null) nlean_free(proof, length);
        }
    }

    internal static byte[] SerializeInput(AggregationInput input)
    {
        if (input.Deps.Count != input.Witnesses.Count || input.Deps.Count > Eip8288Constants.MaxProofDependencies || input.RecursiveProofs.Count > MaxRecursiveInputs || input.Discards.Count > Eip8288Constants.MaxProofDependencies)
            throw new ArgumentException("Invalid Lean aggregation input", nameof(input));
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(input.Deps.Count);
        for (int i = 0; i < input.Deps.Count; i++)
        {
            WriteDependency(writer, input.Deps[i]);
            WriteBlob(writer, input.Witnesses[i].Span);
        }
        writer.Write(input.RecursiveProofs.Count);
        foreach (RecursiveProofInput child in input.RecursiveProofs)
        {
            if (child.InnerDeps is null || child.Proof.IsEmpty || child.InnerDeps.Count > Eip8288Constants.MaxProofDependencies)
                throw new ArgumentException("Invalid child dependencies", nameof(input));
            writer.Write(child.InnerDeps.Count);
            foreach (FrameDependency dep in child.InnerDeps) WriteDependency(writer, dep);
            WriteBlob(writer, child.Proof.Span);
        }
        writer.Write(input.Discards.Count);
        foreach (FrameDependency dep in input.Discards) WriteDependency(writer, dep);
        if (stream.Length > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large", nameof(input));
        return stream.ToArray();
    }

    private static void WriteDependency(BinaryWriter writer, FrameDependency dep)
    {
        if (writer.BaseStream.Length + Eip8288Constants.DependencyTripleLength > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large");
        Span<byte> bytes = stackalloc byte[96];
        dep.WriteTo(bytes);
        writer.Write(bytes);
    }

    private static void WriteBlob(BinaryWriter writer, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteBlob(writer, bytes.AsSpan());
    }

    private static void WriteBlob(BinaryWriter writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxProofBytes || writer.BaseStream.Length + 4 + bytes.Length > Eip8288Constants.MaxAggregationInputBytes) throw new ArgumentException("Lean aggregation input too large");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool IsUnavailable(Exception e) => e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint nlean_abi_version();
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_limits(uint* output, nuint length);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_aggregated_vk(byte* output);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_leansphincs(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_leanstark(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_verify_recursive(byte* depsHash, byte* vk, nuint vkLen, byte* proof, nuint proofLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int nlean_prove_recursive(byte* depsHash, byte* vk, nuint vkLen, byte* input, nuint inputLen, byte** output, nuint* outputLen);
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void nlean_free(byte* proof, nuint proofLen);
}
