// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2
{
    public static class UInt64Extensions
    {
        /// <summary>
        /// Return the largest integer x such that x**2 is less or equal n.
        /// </summary>
        public static ulong SquareRoot(this ulong value)
        {
            ulong x = value;
            ulong y = (x + 1) / 2;
            while (y < x)
            {
                x = y;
                y = (x + value / x) / 2;
            }

            return x;
        }
    }
}
