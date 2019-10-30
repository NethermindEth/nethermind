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
using System.Numerics;
using Nethermind.Blockchain.Filters;
using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.JsonRpc.Data
{
    public class BlockParameter : IJsonRpcRequest
    {
        public static BlockParameter Earliest = new BlockParameter(BlockParameterType.Earliest);

        public static BlockParameter Pending = new BlockParameter(BlockParameterType.Pending);

        public static BlockParameter Latest = new BlockParameter(BlockParameterType.Latest);

        public BlockParameterType Type { get; set; }
        public long? BlockNumber { get; set; }

        public BlockParameter()
        {
        }
        
        public BlockParameter(BlockParameterType type)
        {
            Type = type;
            BlockNumber = null;
        }
        
        public BlockParameter(long number)
        {
            Type = BlockParameterType.BlockNumber;
            BlockNumber = number;
        }

        public void FromJson(string jsonValue)
        {
            switch (jsonValue)
            {
                case string earliest when string.Equals(earliest, "earliest", StringComparison.InvariantCultureIgnoreCase):
                    Type = BlockParameterType.Earliest;
                    return;
                case string pending when string.Equals(pending, "pending", StringComparison.InvariantCultureIgnoreCase):
                    Type = BlockParameterType.Pending;
                    return;
                case string latest when string.Equals(latest, "latest", StringComparison.InvariantCultureIgnoreCase):
                    Type = BlockParameterType.Latest;
                    return;
                case string empty when string.IsNullOrWhiteSpace(empty):
                    Type = BlockParameterType.Latest;
                    return;
                case null:
                    Type = BlockParameterType.Latest;
                    return;
                default:
                    Type = BlockParameterType.BlockNumber;
                    BlockNumber = LongConverter.FromString(jsonValue.Trim('"'));
                    return;
            }
        }

        public override string ToString()
        {
            return $"{Type}, {BlockNumber}";
        }
        
        public FilterBlock ToFilterBlock()
            => BlockNumber != null
                ? new FilterBlock(BlockNumber ?? 0)
                : new FilterBlock(Type.ToFilterBlockType());
    }
}