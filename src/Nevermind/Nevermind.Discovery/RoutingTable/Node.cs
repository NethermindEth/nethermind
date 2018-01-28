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
using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Discovery.RoutingTable
{
    public class Node
    {
        public Node(byte[] id)
        {
            Id = id;
            IdHash = Keccak.Compute(id).Bytes;
            IdHashText = IdHash.ToString();
        }

        public byte[] Id { get; }
        public byte[] IdHash { get; }
        public string IdHashText { get; }
        public string Host { get; set; }
        public int Port { get; set; }
        public bool IsDicoveryNode { get; set; }

        public override bool Equals(object obj)
        {
            if (obj is Node item)
            {
                return Bytes.UnsafeCompare(IdHash, item.IdHash);
            }
            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return IdHash.GetHashCode();
        }

        public override string ToString()
        {
            return $"Id: {Id}, Host: {Host}, Port: {Port}, IsDiscovery: {IsDicoveryNode}";
        }
    }
}