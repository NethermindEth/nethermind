// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Qbft.Bft;

/// <summary>
/// Reads and rewrites the BFT consensus fields of blocks: proposer, validators, vote, committers and
/// the round; also computes the two digests consensus messages refer to.
/// </summary>
/// <remarks>
/// Consensus messages identify a proposal by its <see cref="Digest(BlockHeader)"/> (committed-seal
/// digest: round kept, seals cleared), which is what Besu's in-flight <c>QbftBlock.getHash()</c>
/// returns. The on-chain hash (<see cref="BlockHeader.Hash"/>) additionally zeroes the round.
/// </remarks>
public class QbftBlockInterface(IBftExtraDataCodecSelector codecs)
{
    public IBftExtraDataCodec CodecFor(BlockHeader header) =>
        header is QbftBlockHeader qbft ? qbft.Codec : codecs.ForBlock(header.Number);

    public BftExtraData GetExtraData(BlockHeader header) =>
        header is QbftBlockHeader qbft ? qbft.GetBftExtraData() : CodecFor(header).Decode(header.ExtraData);

    public static Address GetProposer(BlockHeader header) => header.Beneficiary ?? Address.Zero;

    public ValidatorVote? ExtractVote(BlockHeader header)
    {
        Vote? vote = GetExtraData(header).Vote;
        return vote is null ? null : new ValidatorVote(vote.Type, GetProposer(header), vote.Recipient);
    }

    public IReadOnlyList<Address> ValidatorsInBlock(BlockHeader header) => GetExtraData(header).Validators;

    public Address[] GetCommitters(BlockHeader header)
    {
        BftExtraData extraData = GetExtraData(header);
        return BftBlockHashing.RecoverCommitters(header, extraData, CodecFor(header));
    }

    /// <summary>Digest validators sign to commit the block: extra data with the round kept and the seals cleared.</summary>
    public ValueHash256 Digest(BlockHeader header) => BftBlockHashing.CalculateCommitSealDigest(header, GetExtraData(header), CodecFor(header));

    public ValueHash256 Digest(Block block) => Digest(block.Header);

    /// <summary>Copy of <paramref name="block"/> whose extra data carries <paramref name="round"/>; seals, vote and validators are unchanged.</summary>
    public Block ReplaceRound(Block block, int round)
    {
        BftExtraData extraData = GetExtraData(block.Header);
        return extraData.Round == round ? block : WithExtraData(block, extraData.WithRound(round));
    }

    /// <summary>The block as it goes on chain: <paramref name="seals"/> and <paramref name="round"/> written into the extra data.</summary>
    public Block CreateSealedBlock(Block block, int round, IReadOnlyList<Signature> seals) =>
        WithExtraData(block, GetExtraData(block.Header).WithSeals(seals, round));

    private Block WithExtraData(Block block, BftExtraData extraData)
    {
        IBftExtraDataCodec codec = CodecFor(block.Header);
        QbftBlockHeader header = QbftBlockHeader.UpgradeFrom(block.Header, codec);
        if (ReferenceEquals(header, block.Header))
        {
            header = (QbftBlockHeader)header.Clone();
        }

        header.ExtraData = codec.Encode(extraData);
        header.Hash = new Hash256(header.CalculateHash());
        return block.WithReplacedHeader(header);
    }
}
