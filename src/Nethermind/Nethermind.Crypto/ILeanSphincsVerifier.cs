// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Crypto;

/// <summary>Verifies leanSPHINCS signatures, the EIP-8141 <c>LEANSPHINCS_VERIFIER</c> protocol verifier.</summary>
/// <remarks>leanSPHINCS is SLH-DSA (FIPS 205) instantiated with Keccak-256 and WOTS+C, as fixed by the
/// <c>Cryptography/Sphincs</c> module of <c>ethereum/EthLib</c>.</remarks>
public interface ILeanSphincsVerifier
{
    /// <summary>Whether <paramref name="signature"/> is a valid signature of <paramref name="message"/> under
    /// <paramref name="publicKey"/>, its 16-byte <c>PK.seed</c> followed by its 16-byte <c>PK.root</c>.</summary>
    bool Verify(in ValueHash256 publicKey, in ValueHash256 message, ReadOnlySpan<byte> signature);
}

/// <summary>The <see cref="ILeanSphincsVerifier"/> of a node without a leanSPHINCS implementation: it verifies nothing,
/// so a transaction carrying a leanSPHINCS claim is refused rather than accepted unchecked.</summary>
public sealed class UnavailableLeanSphincsVerifier : ILeanSphincsVerifier
{
    public static readonly UnavailableLeanSphincsVerifier Instance = new();

    private UnavailableLeanSphincsVerifier() { }

    public bool Verify(in ValueHash256 publicKey, in ValueHash256 message, ReadOnlySpan<byte> signature) => false;
}
