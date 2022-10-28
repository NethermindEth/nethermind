//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;

namespace Nethermind.Abi
{
    public abstract partial class AbiType
    {
        public static AbiDynamicBytes DynamicBytes => AbiDynamicBytes.Instance;
        public static AbiBytes Bytes32 => AbiBytes.Bytes32;
        public static AbiAddress Address => AbiAddress.Instance;
        public static AbiFunction Function => AbiFunction.Instance;
        public static AbiBool Bool => AbiBool.Instance;
        public static AbiInt Int8 => AbiInt.Int8;
        public static AbiInt Int16 => AbiInt.Int16;
        public static AbiInt Int32 => AbiInt.Int32;
        public static AbiInt Int64 => AbiInt.Int64;
        public static AbiInt Int96 => AbiInt.Int96;
        public static AbiInt Int256 => AbiInt.Int256;
        public static AbiUInt UInt8 => AbiUInt.UInt8;
        public static AbiUInt UInt16 => AbiUInt.UInt16;
        public static AbiUInt UInt32 => AbiUInt.UInt32;
        public static AbiUInt UInt64 => AbiUInt.UInt64;
        public static AbiUInt UInt96 => AbiUInt.UInt96;
        public static AbiUInt UInt256 => AbiUInt.UInt256;
        public static AbiString String => AbiString.Instance;
        public static AbiFixed Fixed { get; } = new(128, 18);
        public static AbiUFixed UFixed { get; } = new(128, 18);

        public virtual bool IsDynamic => false;

        public abstract string Name { get; }

        public abstract (object, int) Decode(byte[] data, int position, bool packed);

        public abstract byte[] Encode(object? arg, bool packed);

        public override string ToString() => Name;

        public override int GetHashCode() => Name.GetHashCode();

        public override bool Equals(object? obj) => obj is AbiType type && Name == type.Name;

        protected string AbiEncodingExceptionMessage => $"Argument cannot be encoded by {GetType().Name}";

        public abstract Type CSharpType { get; }
    }
}
