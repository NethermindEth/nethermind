// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Discv4.Serializers;

public sealed class EnrResponseMsgSerializer(IEcdsa ecdsa, [KeyFilter(IProtectedPrivateKey.NodeKey)] IPrivateKeyGenerator nodeKey, INodeIdResolver nodeIdResolver) : DiscoveryMsgSerializerBase(ecdsa, nodeKey, nodeIdResolver), IMessageSerializer<EnrResponseMsg>
{
    private readonly NodeRecordSigner _nodeRecordSigner = new(ecdsa, nodeKey.Generate());

    public int Serialize(Span<byte> destination, EnrResponseMsg msg)
    {
        GetDataLength(msg, out int contentLength);

        RlpWriter writer = StartPacket(destination, (byte)msg.MsgType);
        writer.StartSequence(contentLength);
        writer.Encode(msg.RequestKeccak);
        msg.NodeRecord.Encode(ref writer);

        return CompletePacket(destination, in writer, out _);
    }

    public EnrResponseMsg Deserialize(ReadOnlySpan<byte> msgBytes)
    {
        (PublicKey? farPublicKey, _) = PrepareForDeserialization(msgBytes, out ReadOnlySpan<byte> data);
        RlpReader ctx = new(data);
        ctx.ReadSequenceLength();
        Hash256 requestKeccak = ctx.DecodeKeccak(); // skip (not sure if needed to verify)

        int positionForHex = ctx.Position;
        NodeRecord nodeRecord = _nodeRecordSigner.Deserialize(ref ctx);
        if (!_nodeRecordSigner.Verify(nodeRecord))
        {
            string resHex = data[..positionForHex].ToHexString();
            throw new NetworkingException($"Invalid ENR signature: {resHex}", NetworkExceptionType.Discovery);
        }

        EnrResponseMsg msg = new(farPublicKey, nodeRecord, requestKeccak);
        return msg;
    }

    public int GetLength(EnrResponseMsg msg) => GetPacketLength(GetDataLength(msg, out _));

    private static int GetDataLength(EnrResponseMsg msg, out int contentLength)
    {
        contentLength = Rlp.LengthOfKeccakRlp;
        contentLength += msg.NodeRecord.GetRlpLengthWithSignature();
        return Rlp.LengthOfSequence(contentLength);
    }
}
