// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

/// <summary>An EIP-8437 mode-2 raw witness <c>[public-key, message, signature]</c> for a recognized leaf program.</summary>
/// <remarks>The message is at most <c>MempoolWrapper.MaxRawMessageBytes</c> bytes; which lengths a program takes is its native check's call.</remarks>
public sealed record RawSignature(byte[] PublicKey, byte[] Message, byte[] Signature);

/// <summary>Native checks and leaf proving for the recognized signature programs this node enables (EIP-8437 mode 2).</summary>
/// <remarks>Recognition is local policy: the enabled programs and their keys are fixed for the process, so a raw verdict never
/// outlives the recognition it was reached under.</remarks>
public interface IRecognizedProgramVerifier
{
    /// <summary>Whether <paramref name="verificationKey"/> is the EIP-8288 key of an enabled recognized program.</summary>
    bool Recognizes(in ValueHash256 verificationKey);

    /// <summary>Checks <paramref name="signature"/> natively as the program of <paramref name="verificationKey"/> would.</summary>
    /// <param name="claim">The claim the program's leaf would publish for the signature, when it is accepted.</param>
    /// <returns>Whether the key is recognized and both the scheme's reference verification and the leaf's semantics accept.</returns>
    bool Verify(in ValueHash256 verificationKey, RawSignature signature, out ValueHash256 claim);

    /// <summary>Proves one leaf of the program of <paramref name="verificationKey"/> over <paramref name="signatures"/>.</summary>
    /// <returns>The leaf's witness, a proof of <c>get_batch_hash</c> over the signatures' sorted claims.</returns>
    /// <exception cref="System.InvalidOperationException">The key is not recognized, a signature fails its check or claims repeat.</exception>
    /// <exception cref="LeanProverBusyException">The prover cannot take the proof now.</exception>
    byte[] ProveLeaf(in ValueHash256 verificationKey, IReadOnlyList<RawSignature> signatures);
}
