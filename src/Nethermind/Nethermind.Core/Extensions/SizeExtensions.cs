// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Extensions;

public static class SizeExtensions
{
    extension(long @this)
    {
        public long GB => @this * 1_000_000_000L;
        public long MB => @this * 1_000_000L;
        public long KB => @this * 1_000L;
        public long GiB => @this * 1024L * 1024L * 1024L;
        public long MiB => @this * 1024L * 1024L;
        public long KiB => @this * 1024L;

        public string SizeToString(bool useSi = false, bool addSpace = false, int precision = 1)
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

    extension(int @this)
    {
        public long GB => ((long)@this).GB;
        public long MB => ((long)@this).MB;
        public long KB => ((long)@this).KB;
        public long GiB => ((long)@this).GiB;
        public long MiB => ((long)@this).MiB;
        public long KiB => ((long)@this).KiB;
    }
}
