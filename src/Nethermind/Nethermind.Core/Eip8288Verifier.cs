// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Core;

/// <summary>
/// The EIP-8288 verifier a fork fixes: <c>AGGREGATED_VK</c> for aggregate proofs, and each enabled scheme's verification
/// parameters for direct witnesses.
/// </summary>
/// <remarks>
/// Peers negotiate no verifier: a node validates wrappers under the fork it predicts for the next slot, inclusion lists
/// under their target block's fork, and blocks under their own fork. Proof verdicts are scoped to the verifier in their key, so
/// a fork that changes <see cref="AggregatedVk"/> invalidates every aggregate verdict while direct-witness verdicts survive as
/// long as their scheme parameters do.
/// </remarks>
public readonly record struct Eip8288Verifier(ValueHash256 AggregatedVk)
{
    /// <summary>The key of the recursive guest pinned by tools/lean-ffi.</summary>
    public static readonly ValueHash256 PinnedAggregatedVk = new(Eip8288Constants.AggregatedVk);

    public static Eip8288Verifier Pinned { get; } = new(PinnedAggregatedVk);

    /// <summary>The verifier fixed by <paramref name="spec"/>.</summary>
    public static Eip8288Verifier For(IReleaseSpec spec) => new(spec.Eip8288AggregatedVk);

    /// <summary>The identifier of the verification parameters for <paramref name="scheme"/>, or the zero hash for a scheme this
    /// verifier does not know.</summary>
    /// <remarks>The backend pins one parameter set per scheme, so a fork can change only <see cref="AggregatedVk"/>.</remarks>
    public ValueHash256 SchemeParams(byte scheme) => scheme switch
    {
        Eip8288Constants.LeanSphincsScheme => SchemeParameters.LeanSphincs,
        Eip8288Constants.LeanStarkScheme => SchemeParameters.LeanStark,
        _ => default
    };

    // Fork construction needs only the aggregate key; defer hashing until direct-witness validation.
    private static class SchemeParameters
    {
        internal static readonly ValueHash256 LeanSphincs = ValueKeccak.Compute("ethereum/8288/leanSPHINCS/pinned-keccak-sphincs"u8);
        internal static readonly ValueHash256 LeanStark = ValueKeccak.Compute("ethereum/8288/leanSTARK/pinned-generic-cpu"u8);
    }

    public override string ToString() => $"AGGREGATED_VK {AggregatedVk.ToShortString()}";
}
