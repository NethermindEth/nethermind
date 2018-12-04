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

namespace Nethermind.JsonRpc
{
    public class BlockParameter : IJsonRpcRequest
    {
        public BlockParameterType Type { get; set; }
        public Quantity BlockId { get; set; }

        public BlockParameter()
        {
        }

        public BlockParameter(BigInteger blockNumber)
        {
            BlockId = new Quantity(blockNumber);
        }
        
        public void FromJson(string jsonValue)
        {
            if (string.IsNullOrEmpty(jsonValue))
            {
                throw new Exception("Empty parameter");
            }
            
            if (Enum.TryParse(jsonValue, true, out BlockParameterType type))
            {
                Type = type;
                return;
            }
            
            Type = BlockParameterType.BlockId;
            BlockId = new Quantity();
            BlockId.FromJson(jsonValue);
        }

        public override string ToString()
        {
            return $"{Type}, {BlockId?.ToJson()}";
        }
    }
}