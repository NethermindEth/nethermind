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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.JsonRpc.Data
{
    public class Data : IJsonRpcResult, IJsonRpcRequest
    {
        public Data()
        {
        }

        public Data(string value)
        {
            Value = value == null ? null : Bytes.FromHexString(value);
        }

        public Data(Bloom bloom)
        {
            Value = bloom?.Bytes;
        }

        public Data(Keccak hash)
        {
            Value = hash?.Bytes;
        }

        public Data(Address address)
        {
            Value = address?.Bytes;
        }

        public Data(byte[] value)
        {
            Value = value;
        }

        public byte[] Value { get; private set; }

        public void FromJson(string jsonValue)
        {
            Value = Bytes.FromHexString(jsonValue);
        }

        public object ToJson()
        {
            return Value?.ToHexString(true);
        }

        public override string ToString()
        {
            return Value?.ToHexString(true);
        }
    }
}