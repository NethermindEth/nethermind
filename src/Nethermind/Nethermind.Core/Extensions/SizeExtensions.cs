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

            // Integer/decimal arithmetic only (no Math.Log/Pow over double):
            // this assembly is linked into the zkEVM guest, which targets a
            // core without an FPU, and System.Decimal is software integer math.
            long bytes = Math.Abs(@this);
            long unit = useSi ? 1000L : 1024L;
            int place = 0;
            long divisor = 1;
            while (place < suf.Length - 1 && bytes >= divisor * unit)
            {
                divisor *= unit;
                place++;
            }

            decimal num = Math.Round((decimal)bytes / divisor, precision);
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

    extension(ulong @this)
    {
        public ulong GB => @this * 1_000_000_000UL;
        public ulong MB => @this * 1_000_000UL;
        public ulong KB => @this * 1_000UL;
        public ulong GiB => @this * 1024UL * 1024UL * 1024UL;
        public ulong MiB => @this * 1024UL * 1024UL;
        public ulong KiB => @this * 1024UL;
    }
}
