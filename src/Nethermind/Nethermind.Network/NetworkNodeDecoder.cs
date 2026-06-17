// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public sealed class NetworkNodeDecoder : RlpDecoder<NetworkNode>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NetworkNode>((int)1.KiB, nameof(NetworkNode.HostIp));

        static NetworkNodeDecoder() => Rlp.RegisterDecoder(typeof(NetworkNode), new NetworkNodeDecoder());

        protected override NetworkNode DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            decoderContext.ReadSequenceLength();

            PublicKey publicKey = new(decoderContext.DecodeByteArraySpan(RlpLimit.L64));
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
            writer.Encode(item.NodeId.Bytes);
            writer.Encode(item.Host);
            writer.Encode(item.Port);
            writer.Encode(string.Empty);
            writer.Encode(item.Reputation);
        }

        public override int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors) => Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));

        private static int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors) => Rlp.LengthOf(item.NodeId.Bytes)
                   + Rlp.LengthOf(item.Host)
                   + Rlp.LengthOf(item.Port)
                   + 1
                   + Rlp.LengthOf(item.Reputation);

        public static void Init()
        {
            // here to register with RLP in static constructor
        }
    }
}
