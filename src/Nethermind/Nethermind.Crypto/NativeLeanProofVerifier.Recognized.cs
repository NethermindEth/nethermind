// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

public sealed unsafe partial class NativeLeanProofVerifier
{
    /// <summary>The EIP-8288 key of the backend's recognized program <paramref name="profile"/>.</summary>
    internal static ValueHash256 RecognizedKey(uint profile)
    {
        byte[] key = new byte[32];
        fixed (byte* k = key)
        {
            try
            {
                if (!Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_recognized_key(profile, k)))
                    throw new ArgumentException($"The native Lean backend has no recognized program {profile}.", nameof(profile));
            }
            catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
        }
        return new ValueHash256(key);
    }

    /// <summary>The backend's native check of <paramref name="signature"/> under recognized program <paramref name="profile"/>.</summary>
    internal static bool VerifyRaw(uint profile, RawSignature signature, out ValueHash256 claim)
    {
        Span<byte> output = stackalloc byte[32];
        bool valid;
        try
        {
            fixed (byte* pk = signature.PublicKey)
            fixed (byte* m = signature.Message.Bytes)
            fixed (byte* s = signature.Signature)
            fixed (byte* c = output)
                valid = Verdict(AbiVersion != ExpectedAbiVersion ? -1 : nlean_verify_raw(profile, pk, (nuint)signature.PublicKey.Length, m, s,
                    (nuint)signature.Signature.Length, c));
        }
        catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
        claim = valid ? new ValueHash256(output) : default;
        return valid;
    }

    /// <summary>Proves one leaf of recognized program <paramref name="profile"/> over <paramref name="signatures"/> on the prover
    /// thread.</summary>
    internal static byte[] ProveRawLeaf(uint profile, IReadOnlyList<RawSignature> signatures)
    {
        if (signatures.Count is 0 or > Eip8288Constants.MaxClaimsPerBatchLeaf) throw new ArgumentException("Invalid raw leaf size", nameof(signatures));
        int length = sizeof(uint);
        foreach (RawSignature signature in signatures) length += 2 * sizeof(uint) + signature.PublicKey.Length + ValueHash256.MemorySize + signature.Signature.Length;
        byte[] input = new byte[length];
        Span<byte> at = input;
        BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)signatures.Count);
        at = at[sizeof(uint)..];
        foreach (RawSignature signature in signatures)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(at, (uint)signature.PublicKey.Length);
            signature.PublicKey.CopyTo(at[sizeof(uint)..]);
            at = at[(sizeof(uint) + signature.PublicKey.Length)..];
            signature.Message.Bytes.CopyTo(at);
            BinaryPrimitives.WriteUInt32LittleEndian(at[ValueHash256.MemorySize..], (uint)signature.Signature.Length);
            signature.Signature.CopyTo(at[(ValueHash256.MemorySize + sizeof(uint))..]);
            at = at[(ValueHash256.MemorySize + sizeof(uint) + signature.Signature.Length)..];
        }
        if (AbiVersion != ExpectedAbiVersion) throw new InvalidOperationException("Incompatible Lean verifier ABI");
        try { return ProverThread.Run(() => ProveRawLeafNative(profile, input)); }
        catch (Exception e) when (IsUnavailable(e)) { throw new InvalidOperationException("Native Lean backend unavailable", e); }
    }

    private static byte[] ProveRawLeafNative(uint profile, byte[] input)
    {
        byte* witness = null;
        nuint length = 0;
        try
        {
            fixed (byte* i = input)
            {
                int result = nlean_prove_raw_leaf(profile, i, (nuint)input.Length, &witness, &length);
                if (result != 1) throw new InvalidOperationException(result == 0 ? "Lean leaf proving rejected its signatures" : $"Lean leaf proving panicked: {LastPanic()}");
            }
            if (witness == null || length > Eip8288Constants.MaxProofBytes) throw new InvalidOperationException("Invalid native Lean leaf buffer");
            return new ReadOnlySpan<byte>(witness, checked((int)length)).ToArray();
        }
        finally
        {
            if (witness != null) nlean_free(witness, length);
        }
    }
}
