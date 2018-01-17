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
using Nevermind.Core;
using Nevermind.Core.Extensions;

namespace Nevermind.JsonRpc.DataModel
{
    public class Quantity : IJsonRpcResult, IJsonRpcRequest
    {
        public Hex Value { get; private set; }

        public Quantity()
        {
        }

        public Quantity(BigInteger value)
        {
            var encodedValue = value.ToBigEndianByteArray();
            Value = encodedValue;
        }

        public Quantity(string value)
        {
            Value = value;
        }

        public BigInteger? GetValue()
        {
            return Value != null ? new BigInteger(Value) : (BigInteger?)null;
        }

        public object ToJson()
        {
            return Value?.ToString(true, true);
        }

        public void FromJson(string jsonValue)
        {
            Value = jsonValue;
        }
    }
}