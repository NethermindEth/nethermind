// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;

public class DependencyProofsMessageSerializer : Eth66SerializerBase<DependencyProofsMessage>
{
    private static readonly RlpLimit CountLimit = RlpLimit.For<DependencyProofsMessage>(
        Eth74Protocol.MaxProofsPerRequest, nameof(DependencyProofsMessage.Proofs));

    private static readonly RlpLimit ProofLimit = RlpLimit.For<DependencyProofsMessage>(
        Eip8141Constants.MaxDependencyProofBytes, nameof(DependencyProofsMessage.Proofs));

    protected override void SerializeInternal(ref RlpWriter writer, DependencyProofsMessage message)
    {
        IOwnedReadOnlyList<byte[]> proofs = message.Proofs;
        RlpSequence sequence = writer.StartSequence(GetContentLength(proofs));
        for (int i = 0; i < proofs.Count; i++)
        {
            writer.Encode(proofs[i]);
        }

        writer.EndSequence(sequence);
    }

    protected override int GetLengthInternal(DependencyProofsMessage message) =>
        Rlp.LengthOfSequence(GetContentLength(message.Proofs));

    protected override DependencyProofsMessage DeserializeInternal(ref RlpReader ctx, long requestId) =>
        new(requestId, ctx.DecodeNonNullArrayPoolList(static (ref RlpReader nested) => nested.DecodeByteArray(ProofLimit), limit: CountLimit));

    private static int GetContentLength(IOwnedReadOnlyList<byte[]> proofs)
    {
        int contentLength = 0;
        for (int i = 0; i < proofs.Count; i++)
        {
            contentLength += Rlp.LengthOf(proofs[i]);
        }

        return contentLength;
    }
}
