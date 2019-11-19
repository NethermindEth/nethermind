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
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz
{
    public static partial class Ssz
    {
        private static void Encode(Span<byte> span, Fork value, ref int offset)
        {
            Encode(span.Slice(offset, Fork.SszLength), value);
            offset += Fork.SszLength;
        }

        private static Fork DecodeFork(Span<byte> span, ref int offset)
        {
            Fork fork = DecodeFork(span.Slice(offset, Fork.SszLength));
            offset += Fork.SszLength;
            return fork;
        }
        
        public static void Encode(Span<byte> span, Fork container)
        {
            if (span.Length != Fork.SszLength) ThrowTargetLength<Fork>(span.Length, Fork.SszLength);
            int offset = 0;
            Encode(span, container.PreviousVersion, ref offset);
            Encode(span, container.CurrentVersion, ref offset);
            Encode(span, container.Epoch, ref offset);
        }

        public static Fork DecodeFork(Span<byte> span)
        {
            if (span.Length != Fork.SszLength) ThrowSourceLength<Fork>(span.Length, Fork.SszLength);
            int offset = 0;
            ForkVersion previous = DecodeForkVersion(span, ref offset);
            ForkVersion current = DecodeForkVersion(span, ref offset);
            Epoch epoch = DecodeEpoch(span, ref offset);
            return new Fork(previous, current, epoch);
        }

    }
}