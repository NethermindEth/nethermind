/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;

namespace Nethermind.Network
{
    public class NetworkNodeDecoder : IRlpDecoder<NetworkNode>
    {
        static NetworkNodeDecoder()
        {
            Rlp.Decoders[typeof(NetworkNode)] = new NetworkNodeDecoder();
        }

        public NetworkNode Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();

            PublicKey publicKey = new PublicKey(rlpStream.DecodeByteArray());
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

            var networkNode = new NetworkNode(publicKey, ip != string.Empty ? ip : null, port, reputation);
            return networkNode;
        }

        public Rlp Encode(NetworkNode item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int contentLength = GetContentLength(item, rlpBehaviors);
            RlpStream stream = new RlpStream(Rlp.GetSequenceRlpLength(contentLength));
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
            throw new System.NotImplementedException();
        }

        public int GetLength(NetworkNode item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }

        private int GetContentLength(NetworkNode item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOf(item.NodeId.Bytes)
                   + Rlp.LengthOf(item.NodeId.Bytes)
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