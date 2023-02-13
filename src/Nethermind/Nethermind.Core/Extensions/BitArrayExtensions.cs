// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;

namespace Nethermind.Core.Extensions
{
    public static unsafe partial class BitVector
    {
        public static void SetBits(this BitArray thisArr, bool value, params int[] positions)
        {
            foreach (int pos in positions)
            {
                thisArr.Set(pos, value);
            }
        }

        public static void SetBits(this BitArray thisArr, bool value, Range range)
        {
            int rangeMasked = (1 << range.End.Value) - (1 << range.Start.Value);
            BitArray arr = new BitArray(rangeMasked.ToBigEndianByteArray());
            thisArr.Or(arr);
        }
    }
}
