// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Int256;

namespace Nethermind.Core.Extensions
{
    public static class SizeExtensions
    {
        public static long GB(this long @this)
        {
            return @this * 1_000_000_000L;
        }

        public static long MB(this long @this)
        {
            return @this * 1_000_000L;
        }

        public static long KB(this long @this)
        {
            return @this * 1_000L;
        }

        public static long GiB(this long @this)
        {
            return @this * 1024L * 1024L * 1024L;
        }

        public static long MiB(this long @this)
        {
            return @this * 1024L * 1024L;
        }

        public static long KiB(this long @this)
        {
            return @this * 1024L;
        }

        public static long GB(this int @this)
        {
            return ((long)@this).GB();
        }

        public static long MB(this int @this)
        {
            return ((long)@this).MB();
        }

        public static long KB(this int @this)
        {
            return ((long)@this).KB();
        }

        public static long GiB(this int @this)
        {
            return ((long)@this).GiB();
        }

        public static long MiB(this int @this)
        {
            return ((long)@this).MiB();
        }

        public static long KiB(this int @this)
        {
            return ((long)@this).KiB();
        }
    }
}
