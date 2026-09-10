// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Consensus.Qbft.Messages;

/// <summary>
/// RLP codec for QBFT payloads and messages, byte-compatible with Besu's <c>qbft-core</c> encodings.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>payload signature digest = <c>keccak(RLP[messageType, payload])</c></item>
/// <item>signed payload = <c>RLP[payload, signature(65)]</c></item>
/// <item>Proposal = <c>RLP[signed, RLP[[roundChanges], [prepares]]]</c>; its payload is <c>[seq, round, block, BAL|null]</c> (legacy: no BAL slot)</item>
/// <item>RoundChange = <c>RLP[signed, block|[], BAL|null, [prepares]]</c> (legacy: no BAL slot)</item>
/// </list>
/// Decoding keeps the shape a message arrived in so that re-encoding for signature verification is exact.
/// </remarks>
public sealed class QbftMessageCodec(IRlpDecoder<Block> blockDecoder)
{
    private static readonly int SignatureRlpLength = Rlp.LengthOfByteString(Signature.Size, 0);
    private const int LegacyItemCount = 3;
    private static readonly EthereumEcdsa _ecdsa = new(0);
    private static readonly BlockAccessListDecoder _balDecoder = BlockAccessListDecoder.Instance;

    public QbftMessageCodec() : this(new BlockDecoder(new QbftHeaderDecoder())) { }

    public byte[] EncodePayload(QbftPayload payload)
    {
        int contentLength = PayloadContentLength(payload);
        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        WritePayload(ref writer, payload, contentLength);
        return result;
    }

    /// <summary>The digest a validator signs: <c>keccak(RLP[messageType, payload])</c>.</summary>
    public ValueHash256 HashForSignature(QbftPayload payload)
    {
        byte[] encoded = EncodePayload(payload);
        KeccakRlpWriter writer = new();
        writer.StartSequence(Rlp.LengthOf(payload.MessageType) + encoded.Length);
        writer.Encode(payload.MessageType);
        writer.Encode(new Rlp(encoded));
        return writer.GetValueHash();
    }

    /// <summary>Recovers the author and rejects high-S signatures, as Besu's <c>SignedData.create</c> does.</summary>
    /// <exception cref="RlpException">The signature does not recover to an address or has a high S value.</exception>
    public SignedData<T> CreateSignedData<T>(T payload, Signature signature) where T : QbftPayload
    {
        if (!signature.HasLowS())
        {
            throw new RlpException("Signature is invalid");
        }

        Address author = _ecdsa.RecoverAddress(signature, HashForSignature(payload)) ?? throw new RlpException("Signature is invalid");
        return new SignedData<T>(payload, signature, author);
    }

    public byte[] Encode(BftMessage message) => message switch
    {
        Proposal proposal => EncodeProposal(proposal),
        RoundChange roundChange => EncodeRoundChange(roundChange),
        Prepare prepare => EncodeSigned(prepare.SignedPayload),
        Commit commit => EncodeSigned(commit.SignedPayload),
        _ => throw new ArgumentException($"Unknown message type {message.GetType().Name}", nameof(message)),
    };

    /// <exception cref="RlpException">Malformed message, unknown code or invalid signature.</exception>
    public BftMessage Decode(int messageCode, ReadOnlySpan<byte> data) => messageCode switch
    {
        QbftMessageCode.Proposal => DecodeProposal(data),
        QbftMessageCode.Prepare => DecodePrepare(data),
        QbftMessageCode.Commit => DecodeCommit(data),
        QbftMessageCode.RoundChange => DecodeRoundChange(data),
        _ => throw new RlpException($"Received message with messageCode={messageCode} does not conform to any recognised QBFT message structure"),
    };

    public Prepare DecodePrepare(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        Prepare prepare = new(ReadSigned(ref reader, ReadPreparePayload));
        reader.CheckEnd();
        return prepare;
    }

    public Commit DecodeCommit(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        Commit commit = new(ReadSigned(ref reader, ReadCommitPayload));
        reader.CheckEnd();
        return commit;
    }

    public Proposal DecodeProposal(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        int end = reader.ReadSequenceLength() + reader.Position;
        SignedData<ProposalPayload> signed = ReadSigned(ref reader, ReadProposalPayload);
        int listsEnd = reader.ReadSequenceLength() + reader.Position;
        IReadOnlyList<SignedData<RoundChangePayload>> roundChanges = ReadSignedList(ref reader, ReadRoundChangePayload);
        IReadOnlyList<SignedData<PreparePayload>> prepares = ReadSignedList(ref reader, ReadPreparePayload);
        reader.Check(listsEnd);
        reader.Check(end);
        reader.CheckEnd();
        return new Proposal(signed, roundChanges, prepares);
    }

    public RoundChange DecodeRoundChange(ReadOnlySpan<byte> data)
    {
        RlpReader reader = new(data);
        int end = reader.ReadSequenceLength() + reader.Position;
        int items = CountItems(reader, end);
        SignedData<RoundChangePayload> signed = ReadSigned(ref reader, ReadRoundChangePayload);
        Block? block;
        if (reader.IsNextItemEmptyList())
        {
            reader.ReadByte();
            block = null;
        }
        else
        {
            block = blockDecoder.Decode(ref reader);
        }

        // Pre-26.1.0 messages have three items, [signed, block, prepares]; the BAL slot sits before the
        // prepares so the item count is the only way to tell the two shapes apart.
        bool legacy = items == LegacyItemCount;
        ReadOnlyBlockAccessList? bal = legacy ? null : ReadBlockAccessList(ref reader, end);
        IReadOnlyList<SignedData<PreparePayload>> prepares = ReadSignedList(ref reader, ReadPreparePayload);
        reader.Check(end);
        reader.CheckEnd();
        return new RoundChange(signed, block, bal, prepares, legacy);
    }

    private byte[] EncodeSigned<T>(SignedData<T> signed) where T : QbftPayload
    {
        int contentLength = SignedContentLength(signed);
        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        WriteSigned(ref writer, signed, contentLength);
        return result;
    }

    private byte[] EncodeProposal(Proposal proposal)
    {
        int signedLength = Rlp.LengthOfSequence(SignedContentLength(proposal.SignedPayload));
        int roundChangesLength = Rlp.LengthOfSequence(SignedListContentLength(proposal.RoundChanges));
        int preparesLength = Rlp.LengthOfSequence(SignedListContentLength(proposal.Prepares));
        int listsContentLength = roundChangesLength + preparesLength;
        int contentLength = signedLength + Rlp.LengthOfSequence(listsContentLength);

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(contentLength);
        WriteSigned(ref writer, proposal.SignedPayload, SignedContentLength(proposal.SignedPayload));
        writer.StartSequence(listsContentLength);
        WriteSignedList(ref writer, proposal.RoundChanges);
        WriteSignedList(ref writer, proposal.Prepares);
        return result;
    }

    private byte[] EncodeRoundChange(RoundChange roundChange)
    {
        int signedContentLength = SignedContentLength(roundChange.SignedPayload);
        int blockLength = roundChange.ProposedBlock is null ? 1 : blockDecoder.GetLength(roundChange.ProposedBlock, RlpBehaviors.None);
        int balLength = roundChange.UseLegacyEncoding ? 0 : BalLength(roundChange.BlockAccessList);
        int preparesLength = Rlp.LengthOfSequence(SignedListContentLength(roundChange.Prepares));
        int contentLength = Rlp.LengthOfSequence(signedContentLength) + blockLength + balLength + preparesLength;

        byte[] result = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = new(result);
        writer.StartSequence(contentLength);
        WriteSigned(ref writer, roundChange.SignedPayload, signedContentLength);
        if (roundChange.ProposedBlock is null)
        {
            writer.EncodeNullObject();
        }
        else
        {
            blockDecoder.Encode(ref writer, roundChange.ProposedBlock);
        }

        if (!roundChange.UseLegacyEncoding)
        {
            WriteBal(ref writer, roundChange.BlockAccessList);
        }

        WriteSignedList(ref writer, roundChange.Prepares);
        return result;
    }

    private int PayloadContentLength(QbftPayload payload)
    {
        int roundLength = Rlp.LengthOf((ulong)payload.RoundIdentifier.Sequence) + Rlp.LengthOf(payload.RoundIdentifier.Round);
        return payload switch
        {
            PreparePayload => roundLength + Rlp.LengthOfKeccakRlp,
            CommitPayload => roundLength + Rlp.LengthOfKeccakRlp + SignatureRlpLength,
            RoundChangePayload rc => roundLength + (rc.PreparedRoundMetadata is null
                ? 1
                : Rlp.LengthOfSequence(Rlp.LengthOf(rc.PreparedRoundMetadata.PreparedRound) + Rlp.LengthOfKeccakRlp)),
            ProposalPayload p => roundLength + blockDecoder.GetLength(p.ProposedBlock, RlpBehaviors.None) + (p.UseLegacyEncoding ? 0 : BalLength(p.BlockAccessList)),
            _ => throw new ArgumentException($"Unknown payload type {payload.GetType().Name}", nameof(payload)),
        };
    }

    private void WritePayload(ref RlpWriter writer, QbftPayload payload, int contentLength)
    {
        writer.StartSequence(contentLength);
        writer.Encode((ulong)payload.RoundIdentifier.Sequence);
        writer.Encode(payload.RoundIdentifier.Round);
        switch (payload)
        {
            case PreparePayload prepare:
                writer.Encode(prepare.Digest);
                break;
            case CommitPayload commit:
                writer.Encode(commit.Digest);
                writer.Encode(BftSignatures.Encode(commit.CommitSeal));
                break;
            case RoundChangePayload roundChange:
                if (roundChange.PreparedRoundMetadata is null)
                {
                    writer.EncodeNullObject();
                }
                else
                {
                    writer.StartSequence(Rlp.LengthOf(roundChange.PreparedRoundMetadata.PreparedRound) + Rlp.LengthOfKeccakRlp);
                    writer.Encode(roundChange.PreparedRoundMetadata.PreparedRound);
                    writer.Encode(roundChange.PreparedRoundMetadata.PreparedBlockHash);
                }

                break;
            case ProposalPayload proposal:
                blockDecoder.Encode(ref writer, proposal.ProposedBlock);
                if (!proposal.UseLegacyEncoding)
                {
                    WriteBal(ref writer, proposal.BlockAccessList);
                }

                break;
        }
    }

    private int SignedContentLength<T>(SignedData<T> signed) where T : QbftPayload =>
        Rlp.LengthOfSequence(PayloadContentLength(signed.Payload)) + SignatureRlpLength;

    private void WriteSigned<T>(ref RlpWriter writer, SignedData<T> signed, int contentLength) where T : QbftPayload
    {
        writer.StartSequence(contentLength);
        WritePayload(ref writer, signed.Payload, PayloadContentLength(signed.Payload));
        writer.Encode(BftSignatures.Encode(signed.Signature));
    }

    private int SignedListContentLength<T>(IReadOnlyList<SignedData<T>> list) where T : QbftPayload
    {
        int length = 0;
        for (int i = 0; i < list.Count; i++)
        {
            length += Rlp.LengthOfSequence(SignedContentLength(list[i]));
        }

        return length;
    }

    private void WriteSignedList<T>(ref RlpWriter writer, IReadOnlyList<SignedData<T>> list) where T : QbftPayload
    {
        writer.StartSequence(SignedListContentLength(list));
        for (int i = 0; i < list.Count; i++)
        {
            WriteSigned(ref writer, list[i], SignedContentLength(list[i]));
        }
    }

    private static int BalLength(ReadOnlyBlockAccessList? bal) => bal is null ? 1 : _balDecoder.GetLength(bal, RlpBehaviors.None);

    private static void WriteBal(ref RlpWriter writer, ReadOnlyBlockAccessList? bal)
    {
        if (bal is null)
        {
            writer.EncodeEmptyByteArray();
        }
        else
        {
            _balDecoder.Encode(ref writer, bal);
        }
    }

    private delegate T PayloadReader<out T>(ref RlpReader reader) where T : QbftPayload;

    private SignedData<T> ReadSigned<T>(ref RlpReader reader, PayloadReader<T> readPayload) where T : QbftPayload
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        T payload = readPayload(ref reader);
        Signature signature = BftSignatures.Decode(reader.DecodeByteArraySpan());
        reader.Check(end);
        return CreateSignedData(payload, signature);
    }

    private IReadOnlyList<SignedData<T>> ReadSignedList<T>(ref RlpReader reader, PayloadReader<T> readPayload) where T : QbftPayload
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        List<SignedData<T>> list = [];
        while (reader.Position < end)
        {
            if (list.Count >= BftMessage.MaxListEntries)
            {
                throw new RlpException($"QBFT message carries more than {BftMessage.MaxListEntries} piggy-backed messages.");
            }

            list.Add(ReadSigned(ref reader, readPayload));
        }

        reader.Check(end);
        return list;
    }

    private static ConsensusRoundIdentifier ReadRound(ref RlpReader reader) => new((long)reader.DecodeULong(), reader.DecodeInt());

    private static PreparePayload ReadPreparePayload(ref RlpReader reader)
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        ConsensusRoundIdentifier round = ReadRound(ref reader);
        Hash256 digest = reader.DecodeKeccak();
        reader.Check(end);
        return new PreparePayload(round, digest);
    }

    private static CommitPayload ReadCommitPayload(ref RlpReader reader)
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        ConsensusRoundIdentifier round = ReadRound(ref reader);
        Hash256 digest = reader.DecodeKeccak();
        Signature seal = BftSignatures.Decode(reader.DecodeByteArraySpan());
        reader.Check(end);
        return new CommitPayload(round, digest, seal);
    }

    private static RoundChangePayload ReadRoundChangePayload(ref RlpReader reader)
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        ConsensusRoundIdentifier round = ReadRound(ref reader);
        int metadataEnd = reader.ReadSequenceLength() + reader.Position;
        PreparedRoundMetadata? metadata = null;
        if (reader.Position < metadataEnd)
        {
            int preparedRound = reader.DecodeInt();
            Hash256 preparedHash = reader.DecodeKeccak();
            metadata = new PreparedRoundMetadata(preparedHash, preparedRound);
        }

        reader.Check(metadataEnd);
        reader.Check(end);
        return new RoundChangePayload(round, metadata);
    }

    private ProposalPayload ReadProposalPayload(ref RlpReader reader)
    {
        int end = reader.ReadSequenceLength() + reader.Position;
        int items = CountItems(reader, end);
        ConsensusRoundIdentifier round = ReadRound(ref reader);
        Block block = blockDecoder.Decode(ref reader) ?? throw new RlpException("Proposal payload carries no block.");
        // Pre-26.1.0 payloads are [seq, round, block]; current ones add a BAL-or-null fourth item.
        bool legacy = items == LegacyItemCount;
        ReadOnlyBlockAccessList? bal = legacy ? null : ReadBlockAccessList(ref reader, end);
        reader.Check(end);
        return new ProposalPayload(round, block, bal, legacy);
    }

    private static ReadOnlyBlockAccessList? ReadBlockAccessList(ref RlpReader reader, int end)
    {
        if (reader.Position >= end)
        {
            return null;
        }

        if (reader.IsNextItemEmptyByteArray())
        {
            reader.ReadByte();
            return null;
        }

        return _balDecoder.Decode(ref reader);
    }

    /// <summary>Counts the items of the list whose content spans up to <paramref name="end"/>, without advancing the caller's reader.</summary>
    private static int CountItems(RlpReader cursor, int end)
    {
        int count = 0;
        while (cursor.Position < end)
        {
            cursor.SkipItem();
            count++;
        }

        return count;
    }
}
