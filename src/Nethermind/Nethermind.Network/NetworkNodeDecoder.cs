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
    public class NetworkNodeDecoder : IRlpStreamDecoder<NetworkNode>, IRlpObjectDecoder<NetworkNode>
    {
        static NetworkNodeDecoder()
        {
            Rlp.Decoders[typeof(NetworkNode)] = new NetworkNodeDecoder();
        }

        public NetworkNode Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();

            PublicKey publicKey = new(rlpStream.DecodeByteArraySpan());
            string ip = rlpStream.DecodeString();
            int port = (int)rlpStream.DecodeByteArraySpan().ReadEthUInt64();
            rlpStream.SkipItem();
            long reputation = 0L;
            try
            {
                reputation = rlpStream.DecodeLong();
            }
            catch (RlpException)
            {
                // regression - old format
            }

            NetworkNode networkNode = new(publicKey, ip != string.Empty ? ip : null, port, reputation);
            return networkNode;
        }

        public void Encode(RlpStream stream, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
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
            return new Rlp(stream.Data);
        }

        public void Encode(MemoryStream stream, NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors)
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
