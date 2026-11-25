// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Test.Crypto;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

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

        BlockSignatureBatch? batch = batched ? new BlockSignatureBatch(BlsSignatureVerifier.Instance) : null;
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
    public void Builder_deposit_registers_only_a_key_that_passes_key_validation([Values] KeyKind kind, [Values] bool customNetwork)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        BeaconChainSpec spec = customNetwork ? NetworkSigningDomainTests.CustomSigningSpec : BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!);
        state.GenesisValidatorsRoot = spec.GenesisValidatorsRoot;
        Bls.SecretKey sk = DeriveKey(310);
        BlsPublicKey key = KeyOf(kind, sk);
        Hash256 credentials = BuilderWithdrawalCredentials(0xC1);
        const ulong amount = 10 * Gwei;
        Hash256 signingRoot = BuilderDepositSigningRoot(spec, key, credentials, amount);
        BlsSignature signature = SignatureFor(kind, Sign(sk, signingRoot));
        AssertPairingHolds(kind, key, signature, signingRoot);
        int buildersBefore = state.Builders!.Length;

        GloasBlockProcessing.ProcessBuilderDepositRequest(state, new BuilderDepositRequest { Pubkey = key, WithdrawalCredentials = credentials, Amount = amount, Signature = signature }, spec);

        Assert.That(state.Builders!.Length, Is.EqualTo(kind == KeyKind.Valid ? buildersBefore + 1 : buildersBefore));
    }

    [Test]
    public void Bls_change_verifies_only_under_a_from_key_that_passes_key_validation([Values] KeyKind kind, [Values] bool gloas, [Values] bool batched, [Values] bool customNetwork)
    {
        const int changing = 4;
        Bls.SecretKey sk = DeriveKey(500);
        BlsPublicKey key = KeyOf(kind, sk);
        BeaconStateGloas gloasState = CreateGloasState(out _, out _);
        BeaconStateFulu fuluState = CreateFuluState(changing + 1);
        BeaconChainSpec spec = customNetwork ? NetworkSigningDomainTests.CustomSigningSpec : BeaconChainSpec.ForGenesisValidatorsRoot(gloasState.GenesisValidatorsRoot!);
        gloasState.GenesisValidatorsRoot = fuluState.GenesisValidatorsRoot = spec.GenesisValidatorsRoot;
        Validator[] validators = gloas ? gloasState.Validators! : fuluState.Validators!;
        Hash256 genesisValidatorsRoot = gloas ? gloasState.GenesisValidatorsRoot! : fuluState.GenesisValidatorsRoot!;
        validators[changing].WithdrawalCredentials = BlsWithdrawalCredentials(key);

        BlsToExecutionChange change = new() { ValidatorIndex = changing, FromBlsPubkey = key, ToExecutionAddress = new Address(Hash(0xE7).Bytes[12..]) };
        Hash256 domain = Domains.ComputeDomain(DomainType.BlsToExecutionChange, spec.GenesisForkVersion, genesisValidatorsRoot);
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(change), domain);
        SignedBlsToExecutionChange signed = new() { Message = change, Signature = SignatureFor(kind, Sign(sk, signingRoot)) };
        AssertPairingHolds(kind, key, signed.Signature, signingRoot);

        BlockSignatureBatch? batch = batched ? new BlockSignatureBatch(BlsSignatureVerifier.Instance) : null;
        Action process = () =>
        {
            if (gloas)
                GloasBlockProcessing.ProcessBlsToExecutionChange(gloasState, signed, verifySignature: true, batch, spec);
            else
                BlockProcessing.ProcessBlsToExecutionChange(fuluState, signed, verifySignature: true, batch, spec);
            batch?.Verify();
        };

        AssertVerdict(kind, process, "Invalid BLS to execution change signature");
    }

    [Test]
    public void A_verifier_that_rejects_every_signature_refuses_a_valid_envelope([Values] bool rejecting, [Values] bool selfBuild)
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        InstallRealValidatorKeys(state);
        SignedExecutionPayloadBid bid = selfBuild
            ? SelfBuildBid(state, state.LatestBlockHash!, Hash(0x88))
            : ValidBuilderBid(state, builderSk, builderIndex: 0, value: 3 * Gwei);
        GloasBlockProcessing.ProcessExecutionPayloadBid(state, bid, SyntheticSpec(), new PubkeyCache(), verifySignature: false);
        SignedExecutionPayloadEnvelope envelope = selfBuild
            ? ValidEnvelope(state, bid.Message!, ValidatorKey((int)state.LatestBlockHeader!.ProposerIndex), Presets.BuilderIndexSelfBuild)
            : ValidEnvelope(state, bid.Message!, builderSk, builderIndex: 0);
        PubkeyCache pubkeys = rejecting ? new PubkeyCache { SignatureVerifier = new RejectingSignatureVerifier() } : new PubkeyCache();

        Action verify = () => GloasBlockProcessing.VerifyExecutionPayloadEnvelope(new BlockStates().Add(state), envelope, new AcceptingNotifier(), pubkeys);

        if (rejecting)
            Assert.That(verify, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo("Invalid execution payload envelope signature"));
        else
            Assert.That(verify, Throws.Nothing);
    }

    [Test]
    public void A_verifier_that_rejects_every_signature_refuses_a_valid_builder_deposit([Values] bool rejecting)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        BeaconChainSpec spec = BeaconChainSpec.ForGenesisValidatorsRoot(state.GenesisValidatorsRoot!);
        if (rejecting)
            spec = spec with { SignatureVerifier = new RejectingSignatureVerifier() };
        Bls.SecretKey sk = DeriveKey(311);
        BlsPublicKey key = KeyOf(KeyKind.Valid, sk);
        Hash256 credentials = BuilderWithdrawalCredentials(0xC2);
        const ulong amount = 10 * Gwei;
        BlsSignature signature = Sign(sk, BuilderDepositSigningRoot(spec, key, credentials, amount));
        int buildersBefore = state.Builders!.Length;

        GloasBlockProcessing.ProcessBuilderDepositRequest(state, new BuilderDepositRequest { Pubkey = key, WithdrawalCredentials = credentials, Amount = amount, Signature = signature }, spec);

        Assert.That(state.Builders!.Length, Is.EqualTo(rejecting ? buildersBefore : buildersBefore + 1));
    }

    private static BlsPublicKey SetBuilderKey(BeaconStateGloas state, KeyKind kind, Bls.SecretKey builderSk) =>
        state.Builders![0].Pubkey = KeyOf(kind, builderSk);

    private static BlsPublicKey KeyOf(KeyKind kind, Bls.SecretKey sk) => kind switch
    {
        KeyKind.Valid => new BlsPublicKey(new Bls.P1(sk).Compress()),
        KeyKind.InfinityWithInfinitySignature => new BlsPublicKey(G1PointAtInfinity()),
        _ => OffSubgroupKeys.WithTorsion(sk),
    };

    private static BlsSignature SignatureFor(KeyKind kind, BlsSignature bySecret) =>
        kind == KeyKind.InfinityWithInfinitySignature ? new BlsSignature(G2PointAtInfinity()) : bySecret;

    private static void AssertPairingHolds(KeyKind kind, BlsPublicKey key, BlsSignature signature, Hash256 signingRoot)
    {
        Bls.P1Affine decoded = new(new long[Bls.P1Affine.Sz]);
        Bls.P2 signaturePoint = new(stackalloc long[Bls.P2.Sz]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.TryDecode(key.Bytes, out _), Is.True);
            Assert.That(signaturePoint.TryDecode(signature.Bytes, out _), Is.True);
        }
        // These fixtures satisfy the pairing even when KeyValidate must reject the public key.
        Bls.PT left = new(stackalloc long[Bls.PT.Sz]);
        left.MillerLoop(signaturePoint.ToAffine(), Bls.P1Affine.Generator(stackalloc long[Bls.P1Affine.Sz]));
        Bls.P2 message = new(stackalloc long[Bls.P2.Sz]);
        message.HashTo(signingRoot.Bytes, "BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_"u8);
        Bls.PT right = new(stackalloc long[Bls.PT.Sz]);
        right.MillerLoop(message.ToAffine(), decoded);
        Assert.That(Bls.PT.FinalVerify(left, right), Is.True,
            $"fixture bug: the {kind} key must satisfy the pairing");
    }

    private static void AssertVerdict(KeyKind kind, Action process, string refusal)
    {
        if (kind == KeyKind.Valid)
            Assert.That(process, Throws.Nothing);
        else
            Assert.That(process, Throws.TypeOf<BeaconStateException>().With.Message.EqualTo(refusal));
    }

    private static Hash256 BuilderDepositSigningRoot(BeaconChainSpec spec, BlsPublicKey key, Hash256 credentials, ulong amount)
    {
        DepositMessage.Merkleize(new DepositMessage { Pubkey = key, WithdrawalCredentials = credentials, Amount = amount }, out UInt256 root);
        Hash256 domain = Domains.ComputeDomain(DomainType.BuilderDeposit, spec.GenesisForkVersion, Hash256.Zero);
        return Domains.ComputeSigningRoot(new Hash256(root.ToLittleEndian()), domain);
    }
}
