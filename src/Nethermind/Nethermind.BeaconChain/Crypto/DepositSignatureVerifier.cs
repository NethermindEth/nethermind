// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
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
    public static bool IsValid(BlsPublicKey pubkey, Hash256 withdrawalCredentials, ulong amount, BlsSignature signature)
    {
        DepositMessage.Merkleize(new DepositMessage
        {
            Pubkey = pubkey,
            WithdrawalCredentials = withdrawalCredentials,
            Amount = amount,
        }, out UInt256 root);

        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, Presets.GenesisForkVersion, Hash256.Zero);
        Hash256 signingRoot = Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);

        Bls.P1Affine publicKey = new(stackalloc long[Bls.P1Affine.Sz]);
        return publicKey.TryDecode(pubkey.Bytes, out _) && BlsSigner.Verify(publicKey, signature.Bytes, signingRoot.Bytes);
    }
}
