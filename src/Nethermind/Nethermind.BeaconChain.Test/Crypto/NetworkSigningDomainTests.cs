// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Crypto;

public class NetworkSigningDomainTests
{
    private static readonly string[] Networks = [nameof(BeaconChainSpec.Mainnet), nameof(BeaconChainSpec.Hoodi), nameof(BeaconChainSpec.Sepolia)];

    [TestCase(nameof(BeaconChainSpec.Mainnet), "0x00000000", "0x03000000")]
    [TestCase(nameof(BeaconChainSpec.Hoodi), "0x10000910", "0x40000910")]
    [TestCase(nameof(BeaconChainSpec.Sepolia), "0x90000069", "0x90000072")]
    public void Config_fork_versions_match_the_network_config(string network, string genesis, string capella)
    {
        BeaconChainSpec spec = Spec(network);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(spec.GenesisForkVersion.ToHexString(true), Is.EqualTo(genesis), "GENESIS_FORK_VERSION");
        Assert.That(spec.CapellaForkVersion.ToHexString(true), Is.EqualTo(capella), "CAPELLA_FORK_VERSION");
        Assert.That(BeaconChainSpec.ForGenesisValidatorsRoot(spec.GenesisValidatorsRoot), Is.SameAs(spec), "a state of this network resolves to it");
    }

    [Test]
    public void A_root_of_no_supported_network_takes_the_mainnet_config_the_spec_vectors_use() =>
        Assert.That(BeaconChainSpec.ForGenesisValidatorsRoot(TestItem.KeccakA), Is.SameAs(BeaconChainSpec.Mainnet));

    [TestCaseSource(nameof(NetworkPairs))]
    public void Deposit_verifies_only_on_the_network_whose_genesis_version_signed_it(string signedFor, string checkedOn)
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(7);
        BlsPublicKey pubkey = new(new Bls.P1(key).Compress());
        Hash256 credentials = GloasTestFixtures.EthWithdrawalCredentials(0xEE);
        const ulong amount = 32_000_000_000;
        DepositMessage.Merkleize(new DepositMessage { Pubkey = pubkey, WithdrawalCredentials = credentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.Deposit, Spec(signedFor).GenesisForkVersion, Hash256.Zero);
        BlsSignature signature = GloasTestFixtures.Sign(key, Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain));

        bool valid = DepositSignatureVerifier.IsValid(Spec(checkedOn).GenesisValidatorsRoot, pubkey, credentials, amount, signature);

        Assert.That(valid, Is.EqualTo(signedFor == checkedOn));
    }

    [TestCaseSource(nameof(NetworkPairs))]
    public void Bls_to_execution_change_verifies_only_on_the_network_whose_genesis_version_signed_it(string signedFor, string checkedOn)
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(8);
        BlsToExecutionChange change = new() { ValidatorIndex = 0, FromBlsPubkey = new BlsPublicKey(new Bls.P1(key).Compress()), ToExecutionAddress = TestItem.AddressA };
        BeaconStateFulu state = new() { GenesisValidatorsRoot = Spec(checkedOn).GenesisValidatorsRoot };
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, Spec(signedFor).GenesisForkVersion, state.GenesisValidatorsRoot);
        SignedBlsToExecutionChange signed = new() { Message = change, Signature = GloasTestFixtures.Sign(key, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain)) };

        Assert.That(SignatureSets.VerifyBlsToExecutionChange(state, signed), Is.EqualTo(signedFor == checkedOn));
    }

    [TestCaseSource(nameof(NetworkPairs))]
    public void Voluntary_exit_verifies_only_on_the_network_whose_capella_version_signed_it(string signedFor, string checkedOn)
    {
        Bls.SecretKey key = GloasTestFixtures.DeriveKey(9);
        BlsPublicKey pubkey = new(new Bls.P1(key).Compress());
        BeaconStateFulu state = new() { GenesisValidatorsRoot = Spec(checkedOn).GenesisValidatorsRoot };
        PubkeyCache pubkeys = new();
        pubkeys.Build([new Validator { Pubkey = pubkey }]);
        VoluntaryExit exit = new() { Epoch = 1, ValidatorIndex = 0 };
        Hash256 domain = Domains.ComputeDomain(DomainType.VoluntaryExit, Spec(signedFor).CapellaForkVersion, state.GenesisValidatorsRoot);
        SignedVoluntaryExit signed = new() { Message = exit, Signature = GloasTestFixtures.Sign(key, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(exit), domain)) };

        Assert.That(SignatureSets.VerifyVoluntaryExit(state, signed, pubkeys), Is.EqualTo(signedFor == checkedOn));
    }

    private static System.Collections.Generic.IEnumerable<TestCaseData> NetworkPairs()
    {
        foreach (string signedFor in Networks)
        {
            foreach (string checkedOn in Networks)
            {
                yield return new TestCaseData(signedFor, checkedOn);
            }
        }
    }

    private static BeaconChainSpec Spec(string network) => network switch
    {
        nameof(BeaconChainSpec.Mainnet) => BeaconChainSpec.Mainnet,
        nameof(BeaconChainSpec.Hoodi) => BeaconChainSpec.Hoodi,
        nameof(BeaconChainSpec.Sepolia) => BeaconChainSpec.Sepolia,
        _ => throw new ArgumentOutOfRangeException(nameof(network)),
    };
}
