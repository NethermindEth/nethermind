// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Crypto;

public class DepositSignatureVerifierTests
{
    private const ulong Amount = 32_000_000_000;

    public enum DepositKey
    {
        Valid,
        InfinityWithInfinitySignature,
        InfinityWithRealSignature,
        Undecodable,
        AllOxff,
    }

    [TestCase(DepositKey.Valid, ExpectedResult = true)]
    [TestCase(DepositKey.InfinityWithInfinitySignature, ExpectedResult = false)]
    [TestCase(DepositKey.InfinityWithRealSignature, ExpectedResult = false)]
    [TestCase(DepositKey.Undecodable, ExpectedResult = false)]
    [TestCase(DepositKey.AllOxff, ExpectedResult = false)]
    public bool Deposit_signature_is_valid_only_for_a_key_that_passes_key_validation(DepositKey kind)
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(7);
        BlsPublicKey pubkey = kind switch
        {
            DepositKey.Valid => new BlsPublicKey(new Bls.P1(key).Compress()),
            DepositKey.InfinityWithInfinitySignature or DepositKey.InfinityWithRealSignature => new BlsPublicKey(GloasTestFixtures.G1PointAtInfinity()),
            // The compression flag is clear, so these bytes never decode as a G1 point.
            DepositKey.Undecodable => GloasTestFixtures.Pubkey(0x57),
            _ => GloasTestFixtures.Pubkey(0xff),
        };
        Hash256 credentials = GloasTestFixtures.EthWithdrawalCredentials(0xEE);
        BlsSignature signature = kind == DepositKey.InfinityWithInfinitySignature
            ? new BlsSignature(GloasTestFixtures.G2PointAtInfinity())
            : GloasTestFixtures.Sign(key, SigningRoot(pubkey, credentials));

        return DepositSignatureVerifier.IsValid(BeaconChainSpec.Mainnet.GenesisValidatorsRoot, pubkey, credentials, Amount, signature);
    }

    [Test]
    public void Deposit_signature_is_invalid_for_a_key_outside_the_subgroup_even_when_the_pairing_holds()
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(7);
        BlsPublicKey pubkey = OffSubgroupKeys.WithTorsion(key);
        Hash256 credentials = GloasTestFixtures.EthWithdrawalCredentials(0xEE);
        Hash256 signingRoot = SigningRoot(pubkey, credentials);
        BlsSignature signature = GloasTestFixtures.Sign(key, signingRoot);

        Bls.P1Affine decoded = new(new long[Bls.P1Affine.Sz]);
        Assert.That(decoded.TryDecode(pubkey.Bytes, out _) && BlsSigner.Verify(decoded, signature.Bytes, signingRoot.Bytes), Is.True,
            "fixture bug: the pairing must accept, so only the subgroup check can refuse");

        Assert.That(DepositSignatureVerifier.IsValid(BeaconChainSpec.Mainnet.GenesisValidatorsRoot, pubkey, credentials, Amount, signature), Is.False);
    }

    // IETF BLS CoreVerify 2.7 requires deposit signatures in G2.
    [Test]
    public void Deposit_signature_outside_G2_is_rejected([Values] bool addTorsion)
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(7);
        BlsPublicKey pubkey = new(new Bls.P1(key).Compress());
        Hash256 credentials = GloasTestFixtures.EthWithdrawalCredentials(0xEE);
        Hash256 signingRoot = SigningRoot(pubkey, credentials);
        BlsSignature signature = addTorsion
            ? OffSubgroupKeys.WithG2Torsion(GloasTestFixtures.Sign(key, signingRoot))
            : OffSubgroupKeys.NotInG2Signature();
        Bls.P2 point = new(new long[Bls.P2.Sz]);
        Assert.That(point.TryDecode(signature.Bytes, out _), Is.True);
        Assert.That(point.OnCurve(), Is.True);
        Assert.That(point.ToAffine().InGroup(), Is.False);

        Assert.That(DepositSignatureVerifier.IsValid(BeaconChainSpec.Mainnet.GenesisValidatorsRoot, pubkey, credentials, Amount, signature), Is.False);
    }

    private static Hash256 SigningRoot(BlsPublicKey pubkey, Hash256 credentials)
    {
        DepositMessage.Merkleize(new DepositMessage { Pubkey = pubkey, WithdrawalCredentials = credentials, Amount = Amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, BeaconChainSpec.Mainnet.GenesisForkVersion, Hash256.Zero);
        return Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
    }
}
