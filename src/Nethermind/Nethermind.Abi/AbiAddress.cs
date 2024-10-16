// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp;

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
                            NettyAbiStream abiStream = new NettyAbiStream(PooledByteBufferAllocator.Default.Buffer(input.Bytes.Length));
                            if (packed)
                            {
                                abiStream.Write(input.Bytes.ToList());
                            }
                            else
                            {
                                abiStrem.Write(input.Bytes.)
                            }
                            return packed ? abiStream.AsSpan().ToArray() : abiStream.AsSpan().ToArray().;
                        }
                    case string stringInput:
                        {
                            arg = new Address(stringInput);
                            continue;
                        }
                    case JsonElement element when element.ValueKind == JsonValueKind.String:
                        {
                            arg = new Address(element.GetString()!);
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
