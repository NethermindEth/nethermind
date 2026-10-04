// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Crypto;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// <c>bls.Verify</c> runs <c>KeyValidate</c> on its key, so a builder key or a <c>from_bls_pubkey</c> at infinity
/// (which pairs with the infinity signature) or outside G1 (which pairs like its subgroup part) must never verify.
/// </summary>
[HardTimeout(60_000)]
public class MessageKeyValidationTests
{
    public enum KeyKind
    {
        Valid,
        InfinityWithInfinitySignature,
        OutsideSubgroupSignedBySecret,
    }

    private const string InvalidBid = "Invalid execution payload bid signature";

    [Test]
    public void Bid_signature_verifies_only_under_a_key_that_passes_key_validation([Values] KeyKind kind, [Values] bool batched)
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        BlsPublicKey key = SetBuilderKey(state, kind, builderSk);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        bid.Signature = SignatureFor(kind, bid.Signature);
        AssertPairingHolds(kind, key, bid.Signature, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(bid.Message!), state.GetDomain(DomainType.BeaconBuilder)));

        BlockSignatureBatch? batch = batched ? new BlockSignatureBatch() : null;
        Action process = () =>
        {
            GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: true, batch);
            batch?.Verify();
        };

        AssertVerdict(kind, process, InvalidBid);
    }

    [Test]
    public void Envelope_signature_verifies_only_under_a_builder_key_that_passes_key_validation([Values] KeyKind kind)
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        BlsPublicKey key = SetBuilderKey(state, kind, builderSk);
        SignedExecutionPayloadBid bid = ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: false);
        SignedExecutionPayloadEnvelope envelope = ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        envelope.Signature = SignatureFor(kind, envelope.Signature);
        AssertPairingHolds(kind, key, envelope.Signature, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(envelope.Message!), state.GetDomain(DomainType.BeaconBuilder)));

        Action verify = () => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), new PubkeyCache());

        AssertVerdict(kind, verify, "Invalid execution payload envelope signature");
    }

    [Test]
    public void Builder_deposit_registers_only_a_key_that_passes_key_validation([Values] KeyKind kind)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        Bls.SecretKey sk = DeriveKey(310);
        BlsPublicKey key = KeyOf(kind, sk);
        Hash256 credentials = BuilderWithdrawalCredentials(0xC1);
        const ulong amount = 10 * Gwei;
        Hash256 signingRoot = BuilderDepositSigningRoot(state, key, credentials, amount);
        BlsSignature signature = SignatureFor(kind, Sign(sk, signingRoot));
        AssertPairingHolds(kind, key, signature, signingRoot);
        int buildersBefore = state.Builders!.Length;

        GloasBlockProcessing.ProcessBuilderDepositRequest(state, new BuilderDepositRequest { Pubkey = key, WithdrawalCredentials = credentials, Amount = amount, Signature = signature });

        Assert.That(state.Builders!.Length, Is.EqualTo(kind == KeyKind.Valid ? buildersBefore + 1 : buildersBefore));
    }

    [Test]
    public void Bls_change_verifies_only_under_a_from_key_that_passes_key_validation([Values] KeyKind kind, [Values] bool gloas, [Values] bool batched)
    {
        const int changing = 4;
        Bls.SecretKey sk = DeriveKey(500);
        BlsPublicKey key = KeyOf(kind, sk);
        BeaconStateGloas gloasState = CreateGloasState(out _, out _);
        BeaconStateFulu fuluState = CreateFuluState(changing + 1);
        Validator[] validators = gloas ? gloasState.Validators! : fuluState.Validators!;
        Hash256 genesisValidatorsRoot = gloas ? gloasState.GenesisValidatorsRoot! : fuluState.GenesisValidatorsRoot!;
        Validator validator = validators[changing].Clone();
        validator.WithdrawalCredentials = BlsWithdrawalCredentials(key);
        validators[changing] = validator;

        BlsToExecutionChange change = new() { ValidatorIndex = changing, FromBlsPubkey = key, ToExecutionAddress = new Address(Hash(0xE7).Bytes[12..]) };
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, BeaconChainSpec.ForGenesisValidatorsRoot(genesisValidatorsRoot).GenesisForkVersion, genesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);
        SignedBlsToExecutionChange signed = new() { Message = change, Signature = SignatureFor(kind, Sign(sk, signingRoot)) };
        AssertPairingHolds(kind, key, signed.Signature, signingRoot);

        BlockSignatureBatch? batch = batched ? new BlockSignatureBatch() : null;
        Action process = () =>
        {
            if (gloas)
                GloasBlockProcessing.ProcessBlsToExecutionChange(gloasState, signed, verifySignature: true, batch);
            else
                BlockProcessing.ProcessBlsToExecutionChange(fuluState, signed, verifySignature: true, batch);
            batch?.Verify();
        };

        AssertVerdict(kind, process, "Invalid BLS to execution change signature");
    }

    private static BlsPublicKey SetBuilderKey(BeaconStateGloas state, KeyKind kind, Bls.SecretKey builderSk)
    {
        Builder builder = state.Builders![0];
        BlsPublicKey key = KeyOf(kind, builderSk);
        state.Builders[0] = new Builder
        {
            Pubkey = key,
            Version = builder.Version,
            ExecutionAddress = builder.ExecutionAddress,
            Balance = builder.Balance,
            DepositEpoch = builder.DepositEpoch,
            WithdrawableEpoch = builder.WithdrawableEpoch,
        };
        return key;
    }

    private static BlsPublicKey KeyOf(KeyKind kind, Bls.SecretKey sk) => kind switch
    {
        KeyKind.Valid => new BlsPublicKey(new Bls.P1(sk).Compress()),
        KeyKind.InfinityWithInfinitySignature => new BlsPublicKey(G1PointAtInfinity()),
        _ => OffSubgroupKeys.WithTorsion(sk),
    };

    private static BlsSignature SignatureFor(KeyKind kind, BlsSignature bySecret) =>
        kind == KeyKind.InfinityWithInfinitySignature ? new BlsSignature(G2PointAtInfinity()) : bySecret;

    /// <summary>The pairing alone accepts every case, so only key validation can refuse the invalid ones.</summary>
    private static void AssertPairingHolds(KeyKind kind, BlsPublicKey key, BlsSignature signature, Hash256 signingRoot)
    {
        Bls.P1Affine decoded = new(new long[Bls.P1Affine.Sz]);
        Assert.That(decoded.TryDecode(key.Bytes, out _) && BlsSigner.Verify(decoded, signature.Bytes, signingRoot.Bytes), Is.True,
            $"fixture bug: the {kind} key must satisfy the pairing");
    }

    private static void AssertVerdict(KeyKind kind, Action process, string refusal)
    {
        if (kind == KeyKind.Valid)
            Assert.That(process, Throws.Nothing);
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo(refusal));
    }

    private static Hash256 BuilderDepositSigningRoot(BeaconStateGloas state, BlsPublicKey key, Hash256 credentials, ulong amount)
    {
        DepositMessage.Merkleize(new DepositMessage { Pubkey = key, WithdrawalCredentials = credentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.BuilderDeposit, BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!).GenesisForkVersion, Hash256.Zero);
        return Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
    }
}
