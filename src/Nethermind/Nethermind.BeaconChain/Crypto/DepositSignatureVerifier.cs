// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Caching;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.BeaconChain.Crypto;

/// <summary>Verifies deposit signatures (proof of possession).</summary>
/// <remarks>
/// Deposits are signed over <c>DOMAIN_DEPOSIT</c> with the genesis fork version and an empty
/// genesis validators root, and the pubkey comes from the deposit itself (it may not be in the
/// registry yet). Invalid deposit signatures do not invalidate a block — the deposit is skipped.
/// </remarks>
public static class DepositSignatureVerifier
{
    private static readonly LruCache<(Hash256 SigningRoot, BlsSignature Signature), bool> Verdicts = new(4096, nameof(DepositSignatureVerifier));
    /// <param name="genesisValidatorsRoot">The root of the state the deposit applies to; it selects the network whose <c>GENESIS_FORK_VERSION</c> signs deposits.</param>
    public static bool IsValid(Hash256 genesisValidatorsRoot, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount, BlsSignature signature)
        => IsValid(BeaconChainSpec.ForGenesisValidatorsRoot(genesisValidatorsRoot), pubkey, withdrawalCredentials, amount, signature);

    internal static bool IsValid(BeaconChainSpec spec, BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount, BlsSignature signature)
    {
        DepositMessage.Merkleize(new DepositMessage
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            Amount = amount,
        }, out UInt256 root);

        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, spec.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);

        if (Verdicts.TryGet((signingRoot, signature), out bool valid))
            return valid;
        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        valid = BlsSignatureSet.TryKeyValidate(pubkey.Bytes, publicKey) && BlsSigner.Verify(publicKey, signature.Bytes, signingRoot.Bytes);
        Verdicts.Set((signingRoot, signature), valid);
        return valid;
    }
}
