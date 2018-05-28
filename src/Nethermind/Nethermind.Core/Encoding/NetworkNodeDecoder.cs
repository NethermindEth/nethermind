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

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class NetworkNodeDecoder : INewRlpDecoder<NetworkNode>
    {
        public NetworkNode Decode(NewRlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            context.ReadSequenceLength();

            var publicKey = new Hex(context.ReadByteArray());
            var ip = System.Text.Encoding.UTF8.GetString(context.ReadByteArray());
            var port = context.ReadByteArray().ToInt32();
            var description = System.Text.Encoding.UTF8.GetString(context.ReadByteArray());
            var reputation = context.ReadByteArray().ToInt64();

            var networkNode = new NetworkNode(publicKey, ip != string.Empty ? ip : null, port, description != string.Empty ? description : null, reputation);
            return networkNode;
        }
    }
}