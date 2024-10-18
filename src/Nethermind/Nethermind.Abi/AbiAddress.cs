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
            NettyAbiStream nettyAbiStream = new NettyAbiStream(PooledByteBufferAllocator.Default.Buffer(1));
            while (true)
            {
                switch (arg)
                {
                    case Address input:
                        {
                            if (packed)
                            {
                                nettyAbiStream.Write(input.Bytes.ToList());
                            }
                            else
                            {
                                nettyAbiStream.Write(input.Bytes.PadLeft(UInt256.LengthInBytes).ToList());
                            }
                            return nettyAbiStream.AsSpan().ToArray();
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
