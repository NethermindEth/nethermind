// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

public sealed class GetProofWrappersMessageSerializer : Eth66SerializerBase<GetProofWrappersMessage>
{
    protected override void SerializeInternal(ref RlpWriter writer, GetProofWrappersMessage message)
    {
        writer.Encode(message.Compact ? 1 : 0);
        WrapperHashes.Write(ref writer, message.Hashes);
    }

    protected override GetProofWrappersMessage DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        int compact = ctx.DecodeInt();
        if (compact is not (0 or 1)) throw new RlpReaderException("GetProofWrappers compact flag must be 0 or 1.");
        return new GetProofWrappersMessage(requestId, WrapperHashes.Read(ref ctx), compact == 1);
    }

    protected override int GetLengthInternal(GetProofWrappersMessage message) =>
        Rlp.LengthOf(message.Compact ? 1 : 0) + WrapperHashes.Length(message.Hashes);
}

public sealed class GetProofWrapperManifestsMessageSerializer : Eth66SerializerBase<GetProofWrapperManifestsMessage>
{
    protected override void SerializeInternal(ref RlpWriter writer, GetProofWrapperManifestsMessage message) =>
        WrapperHashes.Write(ref writer, message.Hashes);

    protected override GetProofWrapperManifestsMessage DeserializeInternal(ref RlpReader ctx, long requestId) =>
        new(requestId, WrapperHashes.Read(ref ctx));

    protected override int GetLengthInternal(GetProofWrapperManifestsMessage message) => WrapperHashes.Length(message.Hashes);
}

/// <summary>The 1 to 32 distinct wrapper hashes of an <c>eth/76</c> request.</summary>
internal static class WrapperHashes
{
    public static void Write(ref RlpWriter writer, IReadOnlyList<ValueHash256> hashes)
    {
        RlpSequence sequence = writer.StartSequence(hashes.Count * Rlp.LengthOfKeccakRlp);
        foreach (ValueHash256 hash in hashes) writer.Encode(hash);
        writer.EndSequence(sequence);
    }

    public static List<ValueHash256> Read(ref RlpReader ctx)
    {
        RlpReader reader = ctx.EnterSequence(out int length);
        int count = reader.CountItems(Eth76Protocol.MaxRequestedWrappers);
        if (count == 0) throw new RlpReaderException("An eth/76 request must name at least one wrapper.");

        List<ValueHash256> hashes = [with(count)];
        HashSet<ValueHash256> distinct = [with(count)];
        while (reader.HasNext)
        {
            ValueHash256 hash = reader.DecodeValueKeccakNonNull();
            if (!distinct.Add(hash)) throw new RlpReaderException("An eth/76 request names a wrapper twice.");
            hashes.Add(hash);
        }

        ctx.ExitSequence(reader, length);
        return hashes;
    }

    public static int Length(IReadOnlyList<ValueHash256> hashes) => Rlp.LengthOfSequence(hashes.Count * Rlp.LengthOfKeccakRlp);
}
