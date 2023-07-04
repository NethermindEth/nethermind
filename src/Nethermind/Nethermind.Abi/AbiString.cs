// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text;

namespace Nethermind.Abi
{
    public class AbiString : AbiType
    {
        public static readonly AbiString Instance = new();

        static AbiString()
        {
            RegisterMapping<string>(Instance);
        }

        private AbiString()
        {
        }

        public override bool IsDynamic => true;

        public override string Name => "string";

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            (object bytes, int newPosition) = DynamicBytes.Decode(data, position, packed);
            return (Encoding.ASCII.GetString((byte[])bytes), newPosition);
        }

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is string input)
            {
                return DynamicBytes.Encode(Encoding.ASCII.GetBytes(input), packed);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override Type CSharpType { get; } = typeof(string);
    }
}
