// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>
/// <see cref="ILeanProofVerifier"/> backed by the native <c>nethermind_lean</c> library
/// (<c>tools/lean-ffi</c>) over a stable C ABI. The native side currently ships a deterministic
/// placeholder; swapping in the real Lean Ethereum (leanSig / leanVM) verifier is a change to the
/// Rust crate only — this wrapper and its callers are unaffected.
/// </summary>
/// <remarks>
/// EIP8288-DEVIATION: not real cryptography yet — see the native crate. This type exists to exercise
/// the full FFI binding path (P/Invoke → native lib) on a live node.
/// </remarks>
public sealed unsafe class NativeLeanProofVerifier : ILeanProofVerifier
{
    private const string Library = "nethermind_lean";

    public static readonly NativeLeanProofVerifier Instance = new();

    /// <summary>ABI version exported by the native library; probe to confirm it loads and matches.</summary>
    public static uint AbiVersion => nlean_abi_version();

    public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        fixed (byte* d = dataHash.Bytes)
        fixed (byte* v = verificationKey.Bytes)
        fixed (byte* w = witness)
        {
            return nlean_verify_leansphincs(d, v, w, (nuint)witness.Length) != 0;
        }
    }

    public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
    {
        fixed (byte* d = dataHash.Bytes)
        fixed (byte* v = verificationKey.Bytes)
        fixed (byte* w = witness)
        {
            return nlean_verify_leanstark(d, v, w, (nuint)witness.Length) != 0;
        }
    }

    public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
    {
        fixed (byte* h = depsHash.Bytes)
        fixed (byte* vk = aggregatedVk)
        fixed (byte* p = proof)
        {
            return nlean_verify_recursive(h, vk, (nuint)aggregatedVk.Length, p, (nuint)proof.Length) != 0;
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint nlean_abi_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nlean_verify_leansphincs(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nlean_verify_leanstark(byte* dataHash, byte* vk, byte* witness, nuint witnessLen);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int nlean_verify_recursive(byte* depsHash, byte* aggregatedVk, nuint aggregatedVkLen, byte* proof, nuint proofLen);
}
