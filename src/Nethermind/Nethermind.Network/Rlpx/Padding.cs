// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
