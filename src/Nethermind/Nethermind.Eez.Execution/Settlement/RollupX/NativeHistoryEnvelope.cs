// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>The dedicated typed history envelope, without producer-supplied execution fields.</summary>
/// <remarks>Decoding does not authenticate ancestry, chain selection or the eligible execution position.</remarks>
internal sealed class NativeHistoryEnvelope
{
    internal const byte Type = 0x46;
    internal const ulong Version = 1;
    private const int HashRlpLength = Hash256.Size + 1;
    private const int PairRlpLength = 43;
    private const int MaxPayloadBytes = 2 * 9 + 3 + NativeHistoryCall.MaxHashes * HashRlpLength;
    private const int MaxPairPayloadBytes = 2 * 9 + 3 + NativeHistoryCall.MaxHashes * PairRlpLength;
    private readonly ValueHash256[] _hashes;
    private readonly NativeHistoryBlock[]? _blocks;
    private readonly int _recordsLength;

    internal ulong ChainId { get; }
    internal bool HasExplicitBlocks => _blocks is not null;
    internal ReadOnlySpan<ValueHash256> Hashes => !HasExplicitBlocks ? _hashes : throw Invalid("explicit blocks require their selected profile");
    internal ReadOnlySpan<NativeHistoryBlock> Blocks => _blocks is not null ? _blocks : throw Invalid("legacy hashes have no explicit numbers");

    private NativeHistoryEnvelope(ulong chainId, ValueHash256[] hashes, NativeHistoryBlock[]? blocks = null)
    {
        ChainId = chainId;
        _hashes = hashes;
        _blocks = blocks;
        if (blocks is null)
            _recordsLength = hashes.Length * HashRlpLength;
        else
            foreach (NativeHistoryBlock block in blocks)
                _recordsLength += Rlp.LengthOfSequence(Rlp.LengthOf(block.Number) + HashRlpLength);
    }

    internal static NativeHistoryEnvelope CreateBlocks(ulong chainId, ReadOnlySpan<NativeHistoryBlock> blocks)
    {
        ValidateBlocks(blocks);
        return new NativeHistoryEnvelope(chainId, [], blocks.ToArray());
    }

    internal static NativeHistoryEnvelope Create(ulong chainId, ReadOnlySpan<ValueHash256> hashes)
    {
        ValidateHashes(hashes);
        return new NativeHistoryEnvelope(chainId, hashes.ToArray());
    }

    internal static NativeHistoryEnvelope Decode(ReadOnlySpan<byte> encoded) => Decode(encoded, false);

    internal static NativeHistoryEnvelope DecodeBlocks(ReadOnlySpan<byte> encoded) => Decode(encoded, true);

    private static NativeHistoryEnvelope Decode(ReadOnlySpan<byte> encoded, bool explicitBlocks)
    {
        int maximum = explicitBlocks ? MaxPairPayloadBytes : MaxPayloadBytes;
        if (encoded.Length < 3 || encoded.Length > maximum + 4 || encoded[0] != Type) throw Invalid("type or envelope length");
        try
        {
            RlpReader reader = new(encoded[1..]);
            int payload = reader.ReadSequenceLength();
            if (payload > maximum || payload != reader.Length - reader.Position) throw Invalid("outer list length");
            if (reader.DecodeULong() != Version) throw Invalid("unsupported version");
            ulong chainId = reader.DecodeULong();
            int length = reader.ReadSequenceLength();
            if (length == 0 || length != reader.Length - reader.Position) throw Invalid("record list length");
            if (explicitBlocks)
            {
                if (length > NativeHistoryCall.MaxHashes * PairRlpLength) throw Invalid("block list length");
                int count = reader.PeekNumberOfItemsRemaining(maxSearch: NativeHistoryCall.MaxHashes + 1);
                if (count == 0 || count > NativeHistoryCall.MaxHashes) throw Invalid("block count");
                NativeHistoryBlock[] blocks = new NativeHistoryBlock[count];
                for (int index = 0; index < count; index++)
                {
                    int pairLength = reader.ReadSequenceLength();
                    int end = checked(reader.Position + pairLength);
                    blocks[index] = new NativeHistoryBlock(reader.DecodeULong(), reader.DecodeValueKeccakNonNull());
                    reader.Check(end);
                }
                reader.CheckEnd();
                ValidateBlocks(blocks);
                return new NativeHistoryEnvelope(chainId, [], blocks);
            }
            if (length > NativeHistoryCall.MaxHashes * HashRlpLength || length % HashRlpLength != 0) throw Invalid("hash list length");
            ValueHash256[] hashes = new ValueHash256[length / HashRlpLength];
            for (int index = 0; index < hashes.Length; index++)
            {
                ValueHash256 hash = reader.DecodeValueKeccakNonNull();
                if (hash == default) throw Invalid("zero history hash");
                hashes[index] = hash;
            }
            reader.CheckEnd();
            return new NativeHistoryEnvelope(chainId, hashes);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new RlpException("History envelope RLP is invalid.", exception);
        }
    }

    internal int PayloadLength => Rlp.LengthOf(Version) + Rlp.LengthOf(ChainId)
        + Rlp.LengthOfSequence(_recordsLength);

    internal void EncodePayload<TWriter>(ref TWriter writer) where TWriter : struct, IRlpWriteBackend, allows ref struct
    {
        writer.StartSequence(PayloadLength);
        writer.Encode(Version);
        writer.Encode(ChainId);
        writer.StartSequence(_recordsLength);
        if (_blocks is null)
            foreach (ValueHash256 hash in _hashes) writer.Encode(hash);
        else
            foreach (NativeHistoryBlock block in _blocks)
            {
                writer.StartSequence(Rlp.LengthOf(block.Number) + HashRlpLength);
                writer.Encode(block.Number);
                writer.Encode(block.Hash);
            }
    }

    internal byte[] Encode()
    {
        byte[] encoded = new byte[1 + Rlp.LengthOfSequence(PayloadLength)];
        encoded[0] = Type;
        RlpWriter writer = new(encoded.AsSpan(1));
        EncodePayload(ref writer);
        return encoded;
    }

    private static void ValidateHashes(ReadOnlySpan<ValueHash256> hashes)
    {
        if (hashes.IsEmpty || hashes.Length > NativeHistoryCall.MaxHashes) throw Invalid("hash count");
        foreach (ValueHash256 hash in hashes)
            if (hash == default) throw Invalid("zero history hash");
    }

    private static void ValidateBlocks(ReadOnlySpan<NativeHistoryBlock> blocks)
    {
        if (blocks.IsEmpty || blocks.Length > NativeHistoryCall.MaxHashes) throw Invalid("block count");
        ulong previous = 0;
        foreach (NativeHistoryBlock block in blocks)
        {
            if (block.Number <= previous || block.Hash == default) throw Invalid("history block number or hash");
            previous = block.Number;
        }
    }

    private static RlpException Invalid(string reason) => new($"Invalid native history envelope: {reason}.");
}

internal readonly record struct NativeHistoryBlock(ulong Number, ValueHash256 Hash);
