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

namespace Nethermind.Abi
{
    public abstract class AbiType
    {
        public static AbiDynamicBytes DynamicBytes { get; } = AbiDynamicBytes.Instance;

        public static AbiAddress Address { get; } = AbiAddress.Instance;

        public static AbiFunction Function { get; } = AbiFunction.Instance;

        public static AbiBool Bool { get; } = AbiBool.Instance;

        public static AbiInt Int256 { get; } = new AbiInt(256);

        public static AbiUInt UInt256 { get; } = new AbiUInt(256);
        
        public static AbiUInt UInt32 { get; } = new AbiUInt(32);
        
        public static AbiUInt UInt16 { get; } = new AbiUInt(16);
        
        public static AbiUInt UInt96 { get; } = new AbiUInt(96);

        public static AbiString String { get; } = AbiString.Instance;

        public static AbiFixed Fixed { get; } = new AbiFixed(128, 19);

        public static AbiUFixed UFixed { get; } = new AbiUFixed(128, 19);

        public virtual bool IsDynamic => false;

        public abstract string Name { get; }

        public virtual bool Validate(byte[] data)
        {
            return true;
        }

        public abstract (object, int) Decode(byte[] data, int position, bool packed);

        public abstract byte[] Encode(object arg, bool packed);

        public override string ToString()
        {
            return Name;
        }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            AbiType type = obj as AbiType;
            return type != null &&
                   Name == type.Name;
        }

        protected string AbiEncodingExceptionMessage => $"Argument cannot be encoded by { GetType().Name}";

        public abstract Type CSharpType { get; }
    }
}