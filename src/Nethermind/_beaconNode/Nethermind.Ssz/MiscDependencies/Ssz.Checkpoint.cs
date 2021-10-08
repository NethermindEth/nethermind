//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Runtime.CompilerServices;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        public const int CheckpointLength = Ssz.RootLength + Ssz.EpochLength;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Encode(Span<byte> span, Checkpoint value, ref int offset)
        {
            Encode(span.Slice(offset, Ssz.CheckpointLength), value);
            offset += Ssz.CheckpointLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Encode(Span<byte> span, Checkpoint container)
        {
            if (span.Length != Ssz.CheckpointLength) ThrowTargetLength<Checkpoint>(span.Length, Ssz.CheckpointLength);
            int offset = 0;
            Encode(span, container.Epoch, ref offset);
            Encode(span, container.Root, ref offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Checkpoint DecodeCheckpoint(ReadOnlySpan<byte> span, ref int offset)
        {
            Checkpoint checkpoint = DecodeCheckpoint(span.Slice(offset, Ssz.CheckpointLength));
            offset += Ssz.CheckpointLength;
            return checkpoint;
        }
        
        public static Checkpoint DecodeCheckpoint(ReadOnlySpan<byte> span)
        {
            if (span.Length != Ssz.CheckpointLength) ThrowSourceLength<Checkpoint>(span.Length, Ssz.CheckpointLength);
            int offset = 0;
            Epoch epoch = DecodeEpoch(span, ref offset);
            Root root = DecodeRoot(span, ref offset);
            return new Checkpoint(epoch, root);
        }
    }
}