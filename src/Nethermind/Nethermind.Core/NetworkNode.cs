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
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core
{
    public class NetworkNode
    {
        public NetworkNode(string enode)
        {
            //enode://0d837e193233c08d6950913bf69105096457fbe204679d6c6c021c36bb5ad83d167350440670e7fec189d80abc18076f45f44bfe480c85b6c632735463d34e4b@89.197.135.74:30303
            string publicKeyString = enode.Substring(8, 128);
            NodeId = new PublicKey(Bytes.FromHexString(publicKeyString));
            string[] address = enode.Substring(8 /* prefix */ + 128 /* public key */ + 1 /* @ */).Split(':');
            Host = address[0];
            Port = int.Parse(address[1]);
        }

        public static NetworkNode[] ParseNodes(string enodesString, ILogger logger)
        {
            string[] nodeStrings = enodesString?.Split(",", StringSplitOptions.RemoveEmptyEntries);
            if (nodeStrings == null)
            {
                return new NetworkNode[0];
            }

            List<NetworkNode> nodes = new List<NetworkNode>();
            foreach (string nodeString in nodeStrings)
            {
                try
                {
                    nodes.Add(new NetworkNode(nodeString.Trim()));
                }
                catch (Exception e)
                {
                    if (logger.IsError) logger.Error($"Could not parse enode data from {nodeString}", e);
                }
            }

            return nodes.ToArray();
        }

        public override string ToString()
        {
            return $"enode://{NodeId?.ToString(false)}@{Host}:{Port}";
        }

        public NetworkNode(string publicKey, string ip, int port, long reputation = 0)
            : this(new PublicKey(publicKey), ip, port, reputation)
        {
        }

        public NetworkNode(PublicKey publicKey, string ip, int port, long reputation = 0)
        {
            NodeId = publicKey;
            Host = ip;
            Port = port;
            Reputation = reputation;
        }

        public PublicKey NodeId { get; }
        public string Host { get; }
        public int Port { get; }
        public long Reputation { get; set; }
    }
}