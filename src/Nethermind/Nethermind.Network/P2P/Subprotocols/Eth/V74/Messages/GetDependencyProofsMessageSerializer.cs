// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;

public class GetDependencyProofsMessageSerializer : Eth66SerializerBase<GetDependencyProofsMessage>
{
    private static readonly RlpLimit RlpLimit = RlpLimit.For<GetDependencyProofsMessage>(
        Eth74Protocol.MaxProofsPerRequest, nameof(GetDependencyProofsMessage.Hashes));

    protected override void SerializeInternal(ref RlpWriter writer, GetDependencyProofsMessage message)
    {
        RlpSequence sequence = writer.StartSequence(GetHashesContentLength(message.Hashes));
        foreach (Hash256 hash in message.Hashes.AsSpan())
        {
            writer.Encode(hash);
        }

        writer.EndSequence(sequence);
    }

    protected override GetDependencyProofsMessage DeserializeInternal(ref RlpReader ctx, long requestId)
    {
        ArrayPoolList<Hash256> hashes =
            ctx.DecodeNonNullArrayPoolList(static (ref RlpReader nestedContext) => nestedContext.DecodeKeccak(), limit: RlpLimit);
        return new GetDependencyProofsMessage(requestId, hashes);
    }

    protected override int GetLengthInternal(GetDependencyProofsMessage message)
        => Rlp.LengthOfSequence(GetHashesContentLength(message.Hashes));

    private static int GetHashesContentLength(IOwnedReadOnlyList<Hash256> hashes) => hashes.Count * Rlp.LengthOfKeccakRlp;
}
