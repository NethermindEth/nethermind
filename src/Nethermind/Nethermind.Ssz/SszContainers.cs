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
    public partial class Ssz
    {
        public static void Encode(Span<byte> span, Fork value)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidTargetLength<Fork>(span.Length, Fork.SszLength);
            }

            Encode(span.Slice(0, ForkVersion.SszLength), value.PreviousVersion);
            Encode(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength), value.CurrentVersion);
            Encode(span.Slice(2 * ForkVersion.SszLength), value.Epoch);
        }
        
        public static Fork DecodeFork(Span<byte> span)
        {
            if (span.Length != Fork.SszLength)
            {
                ThrowInvalidSourceLength<Fork>(span.Length, Fork.SszLength);
            }

            ForkVersion previous = DecodeForkVersion(span.Slice(0, ForkVersion.SszLength));
            ForkVersion current = DecodeForkVersion(span.Slice(ForkVersion.SszLength, ForkVersion.SszLength));
            Epoch epoch = DecodeEpoch(span.Slice(2 * ForkVersion.SszLength));
            
            return new Fork(previous, current, epoch);
        }
    }
}