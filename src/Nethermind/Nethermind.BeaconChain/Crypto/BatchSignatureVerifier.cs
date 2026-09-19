// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Crypto;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>
/// Randomized batch BLS verification over <see cref="BlsSignatureSet"/>. This adds the capability
/// only: <see cref="SignatureSets"/> still verifies every block-processing signature one at a time
/// via <see cref="BlsSigner"/>, and that eager path remains the correctness oracle - it is what
/// <see cref="FindInvalid"/> below calls to attribute a batch failure. Wiring block processing to
/// call <see cref="VerifyBatch"/> instead is separate, later work.
/// </summary>
/// <remarks>
/// A batch is accumulated as one running pairing product and finished with a single final
/// exponentiation via <see cref="Bls.Pairing"/> (<c>blst_pairing_mul_n_aggregate_pk_in_g1</c> per
/// set, then one <c>Commit</c>/<c>FinalVerify</c>) - the primitive the BLS library exposes
/// specifically for this, rather than N independent Miller-loop-plus-final-exponentiation calls.
/// Each set's contribution is scaled by its own independent random scalar before accumulation
/// (Boneh-Drijvers-Neven randomized batch verification). Without that, two invalid sets can be
/// constructed so their pairing terms cancel exactly and an all-invalid batch verifies as valid -
/// see the "cancellation attack" test for a worked example. Randomizing makes that cancellation
/// happen only with probability at most 1/2^ScalarBits, which is why the scalar width matters.
/// </remarks>
public static class BatchSignatureVerifier
{
    /// <summary>
    /// The ciphersuite <see cref="BlsSigner"/> signs and verifies with
    /// (BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_, i.e. hash-to-curve/"RO", not
    /// encode-to-curve/"NU"). The batch path must hash messages to G2 exactly the way the serial
    /// oracle does, or the two would be verifying different statements.
    /// </summary>
    internal static readonly byte[] Cryptosuite = Encoding.UTF8.GetBytes("BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_");

    // >=64 bits is the spec floor for a randomization scalar (cancellation survives with
    // probability at most 1/2^bits); 128 bits leaves headroom for verifying very many batches
    // over a node's lifetime without eroding that margin, at negligible extra pairing cost.
    private const int ScalarBits = 128;
    private const int ScalarBytes = ScalarBits / 8;

    /// <summary>
    /// Verifies every set in one randomized batch. An empty batch has no constraint to violate and
    /// returns true (the same vacuous-truth convention a serial all-of loop over zero sets would
    /// give).
    /// </summary>
    public static bool VerifyBatch(IReadOnlyList<BlsSignatureSet> sets)
    {
        if (sets.Count == 0)
            return true;

        Bls.Pairing pairing = new(hashOrEncode: true, Cryptosuite);
        Span<byte> scalar = stackalloc byte[ScalarBytes];

        foreach (BlsSignatureSet set in sets)
        {
            NextNonZeroScalar(scalar);
            Bls.ERROR error = pairing.MulNAggregate(set.PublicKey, set.Signature, scalar, ScalarBits, set.Message);
            if (error != Bls.ERROR.SUCCESS)
                return false;
        }

        pairing.Commit();
        return pairing.FinalVerify();
    }

    /// <summary>
    /// Serial fallback for attributing a batch failure: re-verifies each set independently through
    /// the same primitive <see cref="SignatureSets"/> uses (<see cref="BlsSigner.Verify"/>), so the
    /// search does not share whatever caused the batch to reject. Returns the index of the first
    /// invalid set, or -1 if every set is valid.
    /// </summary>
    public static int FindInvalid(IReadOnlyList<BlsSignatureSet> sets)
    {
        for (int i = 0; i < sets.Count; i++)
        {
            BlsSignatureSet set = sets[i];
            if (!BlsSigner.Verify(set.PublicKey, new BlsSigner.Signature(set.Signature.ToJacobian()), set.Message))
                return i;
        }

        return -1;
    }

    private static void NextNonZeroScalar(Span<byte> scalar)
    {
        do
        {
            RandomNumberGenerator.Fill(scalar);
        }
        while (scalar.IndexOfAnyExcept((byte)0) < 0);
    }
}
