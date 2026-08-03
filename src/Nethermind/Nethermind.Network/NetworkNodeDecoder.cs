// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public sealed class NetworkNodeDecoder : RlpDecoder<NetworkNode>
    {
        public static NetworkNodeDecoder Instance { get; } = new();

        private static readonly RlpLimit RlpLimit = RlpLimit.For<NetworkNode>((int)1.KiB, nameof(NetworkNode.HostIp));

        protected override NetworkNode DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentEnd = decoderContext.ReadSequenceLength() + decoderContext.Position;
            ReadOnlySpan<byte> firstItem = decoderContext.DecodeByteArraySpan(RlpLimit);
            return IsNodeString(firstItem)
                ? DecodeNodeStringFormat(ref decoderContext, firstItem, contentEnd)
                : DecodeLegacyFormat(ref decoderContext, firstItem);
        }

        private static NetworkNode DecodeNodeStringFormat(ref RlpReader decoderContext, ReadOnlySpan<byte> firstItem, int contentEnd)
        {
            string nodeString = Encoding.UTF8.GetString(firstItem);
            long reputation = decoderContext.DecodeLong();
            decoderContext.Check(contentEnd);
            return new NetworkNode(nodeString)
            {
                Reputation = reputation
            };
        }

        private static NetworkNode DecodeLegacyFormat(ref RlpReader decoderContext, ReadOnlySpan<byte> publicKeyBytes)
        {
            PublicKey publicKey = new(publicKeyBytes);
            string ip = decoderContext.DecodeString(RlpLimit);
            int port = (int)decoderContext.DecodeByteArraySpan(RlpLimit.L8).ReadEthUInt64();
            decoderContext.SkipItem();
            long reputation = 0L;
            try
            {
                reputation = decoderContext.DecodeLong();
            }
            catch (RlpException)
            {
                // regression - old format
            }

            NetworkNode networkNode = new(publicKey, ip != string.Empty ? ip : null, port, reputation);
            return networkNode;
        }

        public override void Encode<TWriter>(ref TWriter writer, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, rlpBehaviors);
            writer.StartSequence(contentLength);
            if (!ShouldEncodeNodeString(item))
            {
                EncodeLegacyFormat(ref writer, item);
                return;
            }

            writer.Encode(item.ToString());
            writer.Encode(item.Reputation);
        }

        public override int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

        private static void EncodeLegacyFormat<TWriter>(ref TWriter writer, NetworkNode item)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            writer.Encode(item.NodeId.Bytes);
            writer.Encode(item.Host);
            writer.Encode(item.Port);
            writer.Encode(string.Empty);
            writer.Encode(item.Reputation);
        }

        private static int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors) => ShouldEncodeNodeString(item)
            ? Rlp.LengthOf(item.ToString())
                + Rlp.LengthOf(item.Reputation)
            : Rlp.LengthOf(item.NodeId.Bytes)
                + Rlp.LengthOf(item.Host)
                + Rlp.LengthOf(item.Port)
                + 1
                + Rlp.LengthOf(item.Reputation);

        private static bool ShouldEncodeNodeString(NetworkNode item) => item.IsEnr || item.DiscoveryPort != item.Port;

        private static bool IsNodeString(ReadOnlySpan<byte> value) =>
            value.Length != PublicKey.LengthInBytes &&
            (value.StartsWith("enr:"u8) || value.StartsWith("enode:"u8));
    }
}
