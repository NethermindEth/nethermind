// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network
{
    public sealed class NetworkNodeDecoder : RlpValueDecoder<NetworkNode>, IRlpObjectDecoder<NetworkNode>
    {
        private static readonly RlpLimit RlpLimit = RlpLimit.For<NetworkNode>((int)1.KiB(), nameof(NetworkNode.HostIp));

        protected override NetworkNode DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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

        public override void Encode(RlpStream stream, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.NodeId.Bytes);
            stream.Encode(item.Host);
            stream.Encode(item.Port);
            stream.Encode(string.Empty);
            stream.Encode(item.Reputation);
        }

        public Rlp Encode(NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, rlpBehaviors);
            RlpStream stream = new(Rlp.LengthOfSequence(contentLength));
            stream.StartSequence(contentLength);
            stream.Encode(item.NodeId.Bytes);
            stream.Encode(item.Host);
            stream.Encode(item.Port);
            stream.Encode(string.Empty);
            stream.Encode(item.Reputation);
            return new Rlp(stream.Data.ToArray());
        }

        public void Encode(MemoryStream stream, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public override int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private static int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOf(item.NodeId.Bytes)
                   + Rlp.LengthOf(item.Host)
                   + Rlp.LengthOf(item.Port)
                   + 1
                   + Rlp.LengthOf(item.Reputation);
        }

        public static void Init()
        {
            // here to register with RLP in static constructor
        }
    }
}
