// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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

        public static string SizeToString(this long @this, bool useSi = false, bool addSpace = false, int precision = 1)
        {
            string[] suf = useSi ? ["B", "KB", "MB", "GB", "TB"] : ["B", "KiB", "MiB", "GiB", "TiB"];
            if (@this == 0)
            {
                return "0" + suf[0];
            }
            long bytes = Math.Abs(@this);
            int place = Math.Min(suf.Length - 1, Convert.ToInt32(Math.Floor(Math.Log(bytes, useSi ? 1000 : 1024))));
            double num = Math.Round(bytes / Math.Pow(useSi ? 1000 : 1024, place), precision);
            return string.Concat(Math.Sign(@this) * num, addSpace ? " " : "", suf[place]);
        }
    }
}
