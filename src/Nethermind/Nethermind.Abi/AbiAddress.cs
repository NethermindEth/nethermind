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
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiAddress : AbiUInt
    {
        private AbiAddress() : base(160)
        {
        }

        public static AbiAddress Instance = new AbiAddress();

        public override string Name => "address";

        public override byte[] Encode(object arg)
        {
            if (arg is Address input)
            {
                byte[] bytes = input.Bytes;
                return UInt.Encode(bytes.ToUnsignedBigInteger());
            }

            if (arg is string stringInput)
            {
                return Encode(new Address(stringInput));
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override (object, int) Decode(byte[] data, int position)
        {
            return (new Address(data.Slice(position + 12, Address.LengthInBytes)), position + UInt.LengthInBytes);
        }
    }
}