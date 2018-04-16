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


using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.Encoding
{
    public class DecodedRlp
    {
        public List<object> Items { get; }

        public object SingleItem { get; }

        public int Length => Items.Count;

        public bool IsSequence => Items != null;

        public DecodedRlp(object item)
        {
            SingleItem = item;
        }

        public DecodedRlp(List<object> items)
        {
            Items = items;
        }

        internal T As<T>()
        {
            if (Items == null)
            {
                return (T)SingleItem;
            }

            Debug.Assert(Items.Count == 0, $"Expected exactly one item in {nameof(DecodedRlp)}");
            return (T)Items[0];
        }

        public Keccak GetKeccak(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? null : new Keccak(bytes);
        }

        public Address GetAddress(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? null : new Address(bytes);
        }

        public BigInteger GetUnsignedBigInteger(int index)
        {
            return ((byte[])Items[index]).ToUnsignedBigInteger();
        }

        public BigInteger GetSignedBigInteger(int index, int byteLength)
        {
            return ((byte[])Items[index]).ToSignedBigInteger(byteLength);
        }

        public byte GetByte(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? (byte)0 : bytes[0];
        }

        public object GetObject(int index)
        {
            return Items[index];
        }

        public int GetInt(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? 0 : bytes.ToInt32();
        }

        public byte[] GetBytes(int index)
        {
            return (byte[])Items[index];
        }

        public string GetString(int index)
        {
            return System.Text.Encoding.UTF8.GetString((byte[])Items[index]);
        }

        public T GetEnum<T>(int index)
        {
            byte[] bytes = (byte[])Items[index];
            return bytes.Length == 0 ? (T)(object)0 : (T)(object)bytes[0];
        }

        public DecodedRlp GetSequence(int index)
        {
            if (Items[index] is byte[])
            {
                return null;
            }

            return (DecodedRlp)Items[index];
        }
    }
}