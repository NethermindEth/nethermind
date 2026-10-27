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
    private const int MaxPayloadBytes = 2 * 9 + 3 + NativeHistoryCall.MaxHashes * HashRlpLength;
    private readonly ValueHash256[] _hashes;

    internal ulong ChainId { get; }
    internal ReadOnlySpan<ValueHash256> Hashes => _hashes;

    private NativeHistoryEnvelope(ulong chainId, ValueHash256[] hashes) => (ChainId, _hashes) = (chainId, hashes);

    internal static NativeHistoryEnvelope Create(ulong chainId, ReadOnlySpan<ValueHash256> hashes)
    {
        ValidateHashes(hashes);
        return new NativeHistoryEnvelope(chainId, hashes.ToArray());
    }

    internal static NativeHistoryEnvelope Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 3 or > MaxPayloadBytes + 4 || encoded[0] != Type) throw Invalid("type or envelope length");
        try
        {
            RlpReader reader = new(encoded[1..]);
            int payload = reader.ReadSequenceLength();
            if (payload > MaxPayloadBytes || payload != reader.Length - reader.Position) throw Invalid("outer list length");
            if (reader.DecodeULong() != Version) throw Invalid("unsupported version");
            ulong chainId = reader.DecodeULong();
            int length = reader.ReadSequenceLength();
            if (length == 0 || length > NativeHistoryCall.MaxHashes * HashRlpLength || length % HashRlpLength != 0 ||
                length != reader.Length - reader.Position) throw Invalid("hash list length");
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

    internal byte[] Encode()
    {
        int hashesLength = _hashes.Length * HashRlpLength;
        int length = Rlp.LengthOf(Version) + Rlp.LengthOf(ChainId) + Rlp.LengthOfSequence(hashesLength);
        byte[] encoded = new byte[1 + Rlp.LengthOfSequence(length)];
        encoded[0] = Type;
        RlpWriter writer = new(encoded.AsSpan(1));
        writer.StartSequence(length);
        writer.Encode(Version);
        writer.Encode(ChainId);
        writer.StartSequence(hashesLength);
        foreach (ValueHash256 hash in _hashes) writer.Encode(hash);
        return encoded;
    }

    private static void ValidateHashes(ReadOnlySpan<ValueHash256> hashes)
    {
        if (hashes.IsEmpty || hashes.Length > NativeHistoryCall.MaxHashes) throw Invalid("hash count");
        foreach (ValueHash256 hash in hashes)
            if (hash == default) throw Invalid("zero history hash");
    }

    private static RlpException Invalid(string reason) => new($"Invalid native history envelope: {reason}.");
}
