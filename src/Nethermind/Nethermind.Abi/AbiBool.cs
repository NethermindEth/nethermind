// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiBool : AbiUInt
    {
        public static readonly AbiBool Instance = new();

        private AbiBool() : base(8)
        {
        }

        public override string Name => "bool";

        public override byte[] Encode(object? arg, bool packed)
        {
            if (arg is bool input)
            {
                Span<byte> bytes = stackalloc byte[1] { input ? (byte)1 : (byte)0 };
                return bytes.PadLeft(packed ? LengthInBytes : UInt256.LengthInBytes);
            }

            throw new AbiException(AbiEncodingExceptionMessage);
        }

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            int length = packed ? LengthInBytes : UInt256.LengthInBytes;
            return (data[position + length - 1] == 1, position + length);
        }

        public override Type CSharpType { get; } = typeof(bool);
    }
}
