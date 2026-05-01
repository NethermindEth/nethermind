// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp;

public sealed class SkipIndexedBlockInfoDecoder : RlpValueDecoder<SkipIndexedBlockInfoEntry>
{
    public static SkipIndexedBlockInfoDecoder Instance { get; } = new();

    public override void Encode(RlpStream stream, SkipIndexedBlockInfoEntry item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int contentLength = GetContentLength(item);
        stream.StartSequence(contentLength);
        stream.Encode(item.SkipCumulativeDifficulty.Value);
        ValueHash256? skipParentHash = item.SkipParentHash;
        stream.Encode(in skipParentHash);
        stream.Encode(item.Difficulty.Value);
        stream.Encode(item.SkipCumulativeDifficulty.IsNegative);
        stream.Encode(item.Difficulty.IsNegative);
        ValueHash256? parentHash = item.ParentHash;
        stream.Encode(in parentHash);
    }

    private static int GetContentLength(SkipIndexedBlockInfoEntry item) =>
        Rlp.LengthOf(item.SkipCumulativeDifficulty.Value)
        + Rlp.LengthOfKeccakRlp
        + Rlp.LengthOf(item.Difficulty.Value)
        + Rlp.LengthOf(item.SkipCumulativeDifficulty.IsNegative)
        + Rlp.LengthOf(item.Difficulty.IsNegative)
        + Rlp.LengthOfKeccakRlp;

    public override int GetLength(SkipIndexedBlockInfoEntry item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
        Rlp.LengthOfSequence(GetContentLength(item));

    protected override SkipIndexedBlockInfoEntry DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
    {
        int lastCheck = decoderContext.ReadSequenceLength() + decoderContext.Position;
        UInt256 skipCumDiffValue = decoderContext.DecodeUInt256();
        ValueHash256 skipParentHash = decoderContext.DecodeValueKeccak() ?? default;
        UInt256 difficultyValue = decoderContext.DecodeUInt256();

        // Sign flags — default to false for backwards compatibility
        bool skipCumDiffNegative = decoderContext.Position < lastCheck && decoderContext.DecodeBool();
        bool difficultyNegative = decoderContext.Position < lastCheck && decoderContext.DecodeBool();

        // ParentHash — appended later; defaults to zero for entries encoded before the field existed.
        ValueHash256 parentHash = decoderContext.Position < lastCheck
            ? decoderContext.DecodeValueKeccak() ?? default
            : default;

        return new SkipIndexedBlockInfoEntry(
            new SignedUInt256(skipCumDiffValue, skipCumDiffNegative),
            skipParentHash,
            new SignedUInt256(difficultyValue, difficultyNegative),
            parentHash);
    }
}
