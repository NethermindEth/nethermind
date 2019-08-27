/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Runtime.CompilerServices;

namespace Nethermind.Network.Rlpx
{
    public static class Frame
    {
        public const int MacSize = 16;
        
        public const int HeaderSize = 16;
        
        public const int BlockSize = 16;
        
        public const int DefaultMaxFrameSize = BlockSize * 64;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculatePadding(int size)
        {
            return size % BlockSize == 0 ? 0 : BlockSize - size % BlockSize;
        }
    }
}