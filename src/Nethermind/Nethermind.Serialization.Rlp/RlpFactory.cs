// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public class RlpFactory
    {
        public long MemorySize => MemorySizes.SmallObjectOverhead
                                    + MemorySizes.Align(MemorySizes.ArrayOverhead + _data.Length)
                                    + MemorySizes.Align(sizeof(int));

        private readonly CappedArray<byte> _data;

        public ref readonly CappedArray<byte> Data => ref _data;

        public RlpFactory(in CappedArray<byte> data)
        {
            _data = data;
        }

        public ValueRlpStream GetRlpStream()
        {
            return new(in _data);
        }
    }
}
