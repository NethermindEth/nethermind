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

using System.Numerics;
using Nethermind.Core.Extensions;

namespace Nethermind.JsonRpc.DataModel
{
    public class Quantity : IJsonRpcResult, IJsonRpcRequest
    {
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
            Value = Bytes.FromHexString(value);
        }

        public byte[] Value { get; private set; }

        // TODO: do we need it? 14/08/2018
        public void FromJson(string jsonValue)
        {
            Value = Bytes.FromHexString(jsonValue);
        }

        public object ToJson()
        {
            return Value?.ToHexString(true);
        }

        // TODO: use UInt256 here?
        public BigInteger? GetValue()
        {
            return Value != null ? new BigInteger(Value, false, true) : (BigInteger?) null;
        }

        public override string ToString()
        {
            return Value.ToHexString(true);
        }
    }
}