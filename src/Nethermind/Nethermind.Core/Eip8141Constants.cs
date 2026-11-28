// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Constants of EIP-8141 frame transactions.
/// https://eips.ethereum.org/EIPS/eip-8141
/// </summary>
public static class Eip8141Constants
{
    public const int MaxFrames = 64;
    public const int MaxClaims = 64;
    public const int MaxClaimWitnessBytes = 1_048_576;
    public const int MaxDependencyProofBytes = 8_388_608;
    public const byte DependencyProofVersionRaw = 0x00;
    public const byte PooledWrapperVersion = 0;
    public const long IntrinsicGasCost = 12_000;
    public const long PerFrameGasCost = 475;
    public const ulong Secp256k1VerificationGasCost = 2_800;
    public const ulong P256VerificationGasCost = 6_700;
    public const ulong LeanSphincsVerificationGasCost = 13_200;
    public const int Secp256k1WitnessLength = 65;
    public const int P256WitnessLength = 128;
    public const int LeanSphincsSignatureSize = 7_740;
    public const int ExpiryDataLength = 8;

    // Shared validation-work floor, not the node's configurable headroom or a block-validity limit.
    public const ulong MaxVerifyGas = 300_000;

    /// <summary>
    /// <c>MAX_PENDING_TXS_USING_NON_CANONICAL_PAYMASTER</c>: the public-mempool cap on the pending frame
    /// transactions one non-canonical paymaster may sponsor.
    /// </summary>
    public const int MaxPendingTxsUsingNonCanonicalPaymaster = 1;

    public static readonly Address EntryPointAddress = new("0x00000000000000000000000000000000000000aa");

    // EIP8141-ISSUE: the spec leaves the three protocol verifier addresses TBD. These placeholders sit outside the
    // active precompile range and outside the 0x0100-0x01ff range EIP-7587 reserves for RIPs.
    public static readonly Address Secp256k1VerifierAddress = new("0x00000000000000000000000000000000000081a1");
    public static readonly Address P256VerifierAddress = new("0x00000000000000000000000000000000000081a2");
    public static readonly Address LeanSphincsVerifierAddress = new("0x00000000000000000000000000000000000081a3");

    /// <summary>EIP-8141 <c>PROTOCOL_VERIFIERS</c>: whether <paramref name="verifier"/> is a claim-only protocol
    /// verifier and, if so, the fixed verification gas and witness length it assigns.</summary>
    public static bool TryGetProtocolVerifier(Address verifier, out ulong verificationGas, out ulong witnessLength)
    {
        (verificationGas, witnessLength) =
            verifier == Secp256k1VerifierAddress ? (Secp256k1VerificationGasCost, (ulong)Secp256k1WitnessLength)
            : verifier == P256VerifierAddress ? (P256VerificationGasCost, (ulong)P256WitnessLength)
            : verifier == LeanSphincsVerifierAddress ? (LeanSphincsVerificationGasCost, (ulong)LeanSphincsSignatureSize)
            : (0UL, 0UL);
        return verificationGas != 0;
    }
    public static readonly Address ExpiryVerifierAddress = new("0x81413f0cF12e9b6a49B1D0439E081c577D57FfFf");

    /// <summary>The expiry-verifier runtime code that the EIP-8141 deployment transaction creates at <see cref="ExpiryVerifierAddress"/>.</summary>
    public static readonly byte[] ExpiryVerifierCode = Bytes.FromHexString("0x60083614600a575f5ffd5b5f3560c01c4211601657005b5f5ffd");

    public static readonly ValueHash256 ExpiryVerifierCodeHash = ValueKeccak.Compute(ExpiryVerifierCode);

    /// <summary>Execution gas a well-formed expiry frame must budget to run: its predeploy target's cold account
    /// access plus what <see cref="ExpiryVerifierCode"/> itself draws on the path that does not revert.</summary>
    /// <remarks>Fixed because every input is: the code is a predeploy and the calldata length is structural. Cold
    /// access is the worst case, a pre-warmed target only costing less. Whether the deadline has passed is a
    /// separate question, and a reverting expiry frame invalidates the transaction whatever it budgeted.
    /// Pinned by <c>Execute_ExpiryFrameAtItsExactCost_IsTheBoundary</c>.</remarks>
    public const ulong ExpiryFrameExecutionGas = Eip8038Constants.ColdAccountAccess + ExpiryVerifierCodeGas;

    private const ulong ExpiryVerifierCodeGas = 51;
}
