// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Messages;
using Nethermind.Consensus.Qbft.StateMachine;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Consensus.Qbft.Test.Messages;

/// <summary>
/// Port of Besu's <c>ProposalTest</c>, <c>RoundChangeTest</c>, <c>PreparePayloadTest</c>, <c>CommitPayloadTest</c>,
/// <c>RoundChangePayloadTest</c> and <c>ProposalPayloadTest</c>: every message survives a wire round trip with its
/// signature still recovering to the author, in both the current and the pre-26.1.0 legacy shape.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class QbftMessageCodecTests
{
    private const ulong Height = 0x1234567890L;
    private static readonly ConsensusRoundIdentifier Round = new((long)Height, 0x7EDCBA98);
    private readonly List<PrivateKey> _keys = QbftTestData.Keys(2);
    private readonly QbftMessageCodec _codec = QbftTestMessages.Codec;

    private MessageFactory Factory(int i = 0, bool legacy = false) => QbftTestMessages.Factory(_keys[i], legacy);
    private Block Block(int round = 0) => QbftTestMessages.Block(Height, _keys[0].Address, QbftTestMessages.Addresses(_keys), round);

    [Test]
    public void PrepareRoundTrips()
    {
        Prepare prepare = Factory().CreatePrepare(Round, Keccak.Compute("digest"));
        Prepare decoded = (Prepare)_codec.Decode(QbftMessageCode.Prepare, _codec.Encode(prepare));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RoundIdentifier, Is.EqualTo(Round));
            Assert.That(decoded.Digest, Is.EqualTo(prepare.Digest));
            Assert.That(decoded.Author, Is.EqualTo(_keys[0].Address));
            Assert.That(decoded.SignedPayload, Is.EqualTo(prepare.SignedPayload));
        }
    }

    [Test]
    public void CommitRoundTrips()
    {
        Block block = Block();
        MessageFactory factory = Factory();
        Commit commit = factory.CreateCommit(Round, QbftTestMessages.Digest(block), factory.CreateCommitSeal(block, Round.Round));
        Commit decoded = (Commit)_codec.Decode(QbftMessageCode.Commit, _codec.Encode(commit));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RoundIdentifier, Is.EqualTo(Round));
            Assert.That(decoded.Digest, Is.EqualTo(commit.Digest));
            Assert.That(decoded.CommitSeal, Is.EqualTo(commit.CommitSeal));
            Assert.That(decoded.Author, Is.EqualTo(_keys[0].Address));
        }
    }

    [Test]
    public void ProposalWithoutCertificatesRoundTrips()
    {
        Block block = Block();
        Proposal proposal = Factory().CreateProposal(Round, block, null, [], []);
        Proposal decoded = (Proposal)_codec.Decode(QbftMessageCode.Proposal, _codec.Encode(proposal));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RoundIdentifier, Is.EqualTo(Round));
            Assert.That(decoded.Block.Hash, Is.EqualTo(block.Hash));
            Assert.That(QbftTestMessages.BlockInterface.Digest(decoded.Block), Is.EqualTo(QbftTestMessages.BlockInterface.Digest(block)));
            Assert.That(decoded.RoundChanges, Is.Empty);
            Assert.That(decoded.Prepares, Is.Empty);
            Assert.That(decoded.Author, Is.EqualTo(_keys[0].Address));
            Assert.That(decoded.BlockAccessList, Is.Null);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ProposalWithCertificatesRoundTrips(bool legacy)
    {
        Block block = Block();
        Hash256 digest = QbftTestMessages.Digest(block);
        ConsensusRoundIdentifier previousRound = new(Round.Sequence, Round.Round - 1);
        SignedData<PreparePayload>[] prepares = [Factory(0, legacy).CreatePrepare(previousRound, digest).SignedPayload, Factory(1, legacy).CreatePrepare(previousRound, digest).SignedPayload];
        PreparedCertificate certificate = new(block, prepares, previousRound.Round);
        SignedData<RoundChangePayload>[] roundChanges = [Factory(0, legacy).CreateRoundChange(Round, certificate).SignedPayload, Factory(1, legacy).CreateRoundChange(Round, null).SignedPayload];

        Proposal proposal = Factory(0, legacy).CreateProposal(Round, block, null, roundChanges, prepares);
        byte[] encoded = _codec.Encode(proposal);
        Proposal decoded = (Proposal)_codec.Decode(QbftMessageCode.Proposal, encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RoundChanges, Is.EqualTo(roundChanges));
            Assert.That(decoded.Prepares, Is.EqualTo(prepares));
            Assert.That(decoded.RoundChanges[0].Payload.PreparedRoundMetadata, Is.EqualTo(new PreparedRoundMetadata(digest, previousRound.Round)));
            Assert.That(decoded.Payload.UseLegacyEncoding, Is.EqualTo(legacy));
            Assert.That(_codec.Encode(decoded), Is.EqualTo(encoded), "re-encoding preserves the wire shape");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundChangeWithPreparedCertificateRoundTrips(bool legacy)
    {
        Block block = Block(2);
        Hash256 digest = QbftTestMessages.Digest(block);
        ConsensusRoundIdentifier preparedRound = new(Round.Sequence, 2);
        SignedData<PreparePayload>[] prepares = [Factory(0, legacy).CreatePrepare(preparedRound, digest).SignedPayload, Factory(1, legacy).CreatePrepare(preparedRound, digest).SignedPayload];
        RoundChange roundChange = Factory(0, legacy).CreateRoundChange(Round, new PreparedCertificate(block, prepares, 2));

        byte[] encoded = _codec.Encode(roundChange);
        RoundChange decoded = (RoundChange)_codec.Decode(QbftMessageCode.RoundChange, encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.RoundIdentifier, Is.EqualTo(Round));
            Assert.That(decoded.PreparedRound, Is.EqualTo(2));
            Assert.That(decoded.PreparedRoundMetadata!.PreparedBlockHash, Is.EqualTo(digest));
            Assert.That(decoded.ProposedBlock!.Hash, Is.EqualTo(block.Hash));
            Assert.That(decoded.Prepares, Is.EqualTo(prepares));
            Assert.That(decoded.UseLegacyEncoding, Is.EqualTo(legacy));
            Assert.That(decoded.BlockAccessList, Is.Null);
            Assert.That(_codec.Encode(decoded), Is.EqualTo(encoded));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RoundChangeWithoutPreparedCertificateRoundTrips(bool legacy)
    {
        RoundChange roundChange = Factory(0, legacy).CreateRoundChange(Round, null);
        byte[] encoded = _codec.Encode(roundChange);
        RoundChange decoded = (RoundChange)_codec.Decode(QbftMessageCode.RoundChange, encoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PreparedRoundMetadata, Is.Null);
            Assert.That(decoded.ProposedBlock, Is.Null);
            Assert.That(decoded.Prepares, Is.Empty);
            Assert.That(decoded.Author, Is.EqualTo(_keys[0].Address));
        }
    }

    [Test]
    public void LegacyRoundChangeIsAThreeItemListAndCurrentIsFour()
    {
        RoundChange legacy = Factory(0, true).CreateRoundChange(Round, null);
        RoundChange current = Factory(0, false).CreateRoundChange(Round, null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountTopLevelItems(_codec.Encode(legacy)), Is.EqualTo(3));
            Assert.That(CountTopLevelItems(_codec.Encode(current)), Is.EqualTo(4));
        }
    }

    [Test]
    public void LegacyProposalPayloadIsAThreeItemListAndCurrentIsFour()
    {
        Block block = Block();
        Proposal legacy = Factory(0, true).CreateProposal(Round, block, null, [], []);
        Proposal current = Factory(0, false).CreateProposal(Round, block, null, [], []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(CountTopLevelItems(_codec.EncodePayload(legacy.Payload)), Is.EqualTo(3));
            Assert.That(CountTopLevelItems(_codec.EncodePayload(current.Payload)), Is.EqualTo(4));
        }
    }

    [Test]
    public void SignatureHashCoversMessageTypeAndPayload()
    {
        PreparePayload prepare = new(Round, Keccak.Compute("digest"));
        byte[] payload = _codec.EncodePayload(prepare);
        byte[] expectedPreimage = new byte[Rlp.LengthOfSequence(1 + payload.Length)];
        RlpWriter writer = new(expectedPreimage);
        writer.StartSequence(1 + payload.Length);
        writer.Encode(QbftMessageCode.Prepare);
        writer.Encode(new Rlp(payload));
        Assert.That(_codec.HashForSignature(prepare), Is.EqualTo(ValueKeccak.Compute(expectedPreimage)));
    }

    [Test]
    public void TamperedPayloadNoLongerRecoversTheAuthor()
    {
        Prepare prepare = Factory().CreatePrepare(Round, Keccak.Compute("digest"));
        SignedData<PreparePayload> tampered = _codec.CreateSignedData(new PreparePayload(Round, Keccak.Compute("other")), prepare.SignedPayload.Signature);
        Assert.That(tampered.Author, Is.Not.EqualTo(_keys[0].Address));
    }

    [Test]
    public void HighSSignatureIsRejected()
    {
        Prepare prepare = Factory().CreatePrepare(Round, Keccak.Compute("digest"));
        Signature original = prepare.SignedPayload.Signature;
        UInt256 s = new(original.S.Span, isBigEndian: true);
        Signature highS = new(new UInt256(original.R.Span, isBigEndian: true), SecP256k1Curve.N - s, (ulong)(Signature.VOffset + (1 - original.RecoveryId)));
        Assert.That(() => _codec.CreateSignedData(prepare.Payload, highS), Throws.InstanceOf<RlpException>().With.Message.Contains("Signature is invalid"));
    }

    [Test]
    public void UnknownMessageCodeThrows() =>
        Assert.That(() => _codec.Decode(0x11, Bytes.FromHexString("c0")), Throws.InstanceOf<RlpException>());

    [Test]
    public void OversizedCertificateListsAreRejected()
    {
        Block block = Block();
        Hash256 digest = QbftTestMessages.Digest(block);
        List<SignedData<PreparePayload>> prepares = [];
        for (int i = 0; i <= BftMessage.MaxListEntries; i++)
        {
            prepares.Add(Factory().CreatePrepare(new ConsensusRoundIdentifier(Round.Sequence, i), digest).SignedPayload);
        }

        Proposal proposal = Factory().CreateProposal(Round, block, null, [], prepares);
        Assert.That(() => _codec.Decode(QbftMessageCode.Proposal, _codec.Encode(proposal)), Throws.InstanceOf<RlpException>());
    }

    private static int CountTopLevelItems(byte[] encoded)
    {
        RlpReader reader = new(encoded);
        int end = reader.ReadSequenceLength() + reader.Position;
        int count = 0;
        while (reader.Position < end)
        {
            reader.SkipItem();
            count++;
        }

        return count;
    }
}
