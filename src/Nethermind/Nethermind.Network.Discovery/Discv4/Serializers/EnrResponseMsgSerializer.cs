// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class EnrResponseMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IZeroMessageSerializer<EnrResponseMsg>
{
    private readonly NodeRecordSigner _nodeRecordSigner = new(ecdsa, nodeKey.Generate());

    public void Serialize(Span<byte> buffer, EnrResponseMsg msg)
    {
        GetLength(msg, out int contentLength);

        PrepareBufferForSerialization(buffer, (byte)msg.MsgType);
        RlpWriter writer = new(buffer.Slice(EnvelopeLength));
        writer.StartSequence(contentLength);
        writer.Encode(msg.RequestKeccak);
        msg.NodeRecord.Encode(ref writer);

        AddSignatureAndMdc(buffer);
    }

    public EnrResponseMsg Deserialize(ReadOnlySpan<byte> data, out int consumed)
    {
        (PublicKey? farPublicKey, _) = PrepareForDeserialization(data);
        ReadOnlySpan<byte> content = data.Slice(EnvelopeLength);
        RlpReader ctx = new(content);
        ctx.ReadSequenceLength();
        Hash256 requestKeccak = ctx.DecodeKeccak(); // skip (not sure if needed to verify)

        int positionForHex = ctx.Position;
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(ref ctx);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            string resHex = content[..positionForHex].ToHexString();
            throw new NetworkingException($"Invalid ENR signature: {resHex}", NetworkExceptionType.Discovery);
        }

        consumed = EnvelopeLength + ctx.Position;
        EnrResponseMsg msg = new(farPublicKey, nodeRecord, requestKeccak);
        return msg;
    }

    public int GetLength(EnrResponseMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOfKeccakRlp;
        contentLength += msg.NodeRecord.GetRlpLengthWithSignature();
        return EnvelopeLength + Rlp.LengthOfSequence(contentLength);
    }
}
