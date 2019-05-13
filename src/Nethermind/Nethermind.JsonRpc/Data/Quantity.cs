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
using System.Globalization;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Json;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.JsonRpc.Data
{
    public class Quantity : IJsonRpcResult, IJsonRpcRequest
    {
        public Quantity()
        {
        }

        public Quantity(BigInteger value)
        {
            Value = value;
        }

        public Quantity(string value)
        {
            FromJson(value);
        }

        public BigInteger? Value { get; private set; }

        public void FromJson(string jsonValue)
        {
            Value = jsonValue.StartsWith("0x")
                ? BigInteger.Parse(jsonValue.Replace("0x", "0"), NumberStyles.AllowHexSpecifier)
                : BigInteger.Parse(jsonValue);
        }

        public object ToJson()
        {
            return Value?.ToBigEndianByteArray()?.ToHexString(true);
        }

        private static BigInteger _maxInt = BigInteger.Pow(2, 256) - 1;
        
        public UInt256? AsNumber()
        {
            if (Value > _maxInt)
            {
                return null;
            }
            
            return Value != null ? (UInt256?)Value : null;
        }

        public override string ToString()
        {
            return Value?.ToBigEndianByteArray()?.ToHexString(true);
        }
    }
}