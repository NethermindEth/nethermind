// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap
{
    public class PathGroup
    {
        public byte[][] Group { get; set; }

        public static RlpItemList EncodeToRlpItemList(IReadOnlyList<PathGroup> groups)
        {
            int totalLen = 0;
            int[] groupContentLens = new int[groups.Count];
            for (int i = 0; i < groups.Count; i++)
            {
                byte[][] paths = groups[i].Group;
                int gcl = 0;
                for (int j = 0; j < paths.Length; j++)
                    gcl += Rlp.LengthOf(paths[j]);
                groupContentLens[i] = gcl;
                totalLen += Rlp.LengthOfSequence(gcl);
            }

            IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(totalLen);
            Memory<byte> region = owner.Memory.Slice(0, totalLen);
            if (MemoryMarshal.TryGetArray(region, out ArraySegment<byte> segment))
            {
                RlpStream stream = new(segment.Array!);
                for (int i = 0; i < groups.Count; i++)
                {
                    byte[][] paths = groups[i].Group;
                    stream.StartSequence(groupContentLens[i]);
                    for (int j = 0; j < paths.Length; j++)
                        stream.Encode(paths[j]);
                }
            }

            return new RlpItemList(owner, region);
        }
    }
}
