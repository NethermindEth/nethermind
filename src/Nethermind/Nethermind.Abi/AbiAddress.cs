// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Abi
{
    public class AbiAddress : AbiUInt
    {
        public static readonly AbiAddress Instance = new();

        private AbiAddress() : base(160)
        {
        }

        public override string Name => "address";

        public override byte[] Encode(object? arg, bool packed)
        {
            while (true)
            {
                switch (arg)
                {
                    case Address input:
                        {
                            byte[] bytes = input.Bytes;
                            return packed ? bytes : bytes.PadLeft(UInt256.LengthInBytes);
                        }
                    case string stringInput:
                        {
                            arg = new Address(stringInput);
                            continue;
                        }
                    default:
                        {
                            throw new AbiException(AbiEncodingExceptionMessage);
                        }
                }
            }
        }

        public override Type CSharpType { get; } = typeof(Address);

        public override (object, int) Decode(byte[] data, int position, bool packed)
        {
            return (new Address(data.Slice(position + (packed ? 0 : 12), Address.LengthInBytes)), position + (packed ? Address.LengthInBytes : UInt256.LengthInBytes));
        }
    }
}
