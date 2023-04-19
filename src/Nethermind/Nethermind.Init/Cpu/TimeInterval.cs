// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

using System.Globalization;

namespace Nethermind.Init.Cpu;

internal readonly struct TimeInterval
{
    public static readonly TimeInterval Nanosecond = TimeUnit.Nanosecond.ToInterval(1L);

    public static readonly TimeInterval Microsecond = TimeUnit.Microsecond.ToInterval(1L);

    public static readonly TimeInterval Millisecond = TimeUnit.Millisecond.ToInterval(1L);

    public static readonly TimeInterval Second = TimeUnit.Second.ToInterval(1L);

    public static readonly TimeInterval Minute = TimeUnit.Minute.ToInterval(1L);

    public static readonly TimeInterval Hour = TimeUnit.Hour.ToInterval(1L);

    public static readonly TimeInterval Day = TimeUnit.Day.ToInterval(1L);

    public double Nanoseconds { get; }

    public TimeInterval(double nanoseconds)
    {
        Nanoseconds = nanoseconds;
    }

    public TimeInterval(double value, TimeUnit unit)
        : this(value * (double)unit.NanosecondAmount)
    {
    }

    public Frequency ToFrequency()
    {
        return new Frequency(Second / this);
    }

    public double ToNanoseconds()
    {
        return this / Nanosecond;
    }

    public double ToMicroseconds()
    {
        return this / Microsecond;
    }

    public double ToMilliseconds()
    {
        return this / Millisecond;
    }

    public double ToSeconds()
    {
        return this / Second;
    }

    public double ToMinutes()
    {
        return this / Minute;
    }

    public double ToHours()
    {
        return this / Hour;
    }

    public double ToDays()
    {
        return this / Day;
    }

    public static TimeInterval FromNanoseconds(double value)
    {
        return Nanosecond * value;
    }

    public static TimeInterval FromMicroseconds(double value)
    {
        return Microsecond * value;
    }

    public static TimeInterval FromMilliseconds(double value)
    {
        return Millisecond * value;
    }

    public static TimeInterval FromSeconds(double value)
    {
        return Second * value;
    }

    public static TimeInterval FromMinutes(double value)
    {
        return Minute * value;
    }

    public static TimeInterval FromHours(double value)
    {
        return Hour * value;
    }

    public static TimeInterval FromDays(double value)
    {
        return Day * value;
    }

    public static double operator /(TimeInterval a, TimeInterval b)
    {
        return 1.0 * a.Nanoseconds / b.Nanoseconds;
    }

    public static TimeInterval operator /(TimeInterval a, double k)
    {
        return new TimeInterval(a.Nanoseconds / k);
    }

    public static TimeInterval operator /(TimeInterval a, int k)
    {
        return new TimeInterval(a.Nanoseconds / (double)k);
    }

    public static TimeInterval operator *(TimeInterval a, double k)
    {
        return new TimeInterval(a.Nanoseconds * k);
    }

    public static TimeInterval operator *(TimeInterval a, int k)
    {
        return new TimeInterval(a.Nanoseconds * (double)k);
    }

    public static TimeInterval operator *(double k, TimeInterval a)
    {
        return new TimeInterval(a.Nanoseconds * k);
    }

    public static TimeInterval operator *(int k, TimeInterval a)
    {
        return new TimeInterval(a.Nanoseconds * (double)k);
    }

    public static bool operator <(TimeInterval a, TimeInterval b)
    {
        return a.Nanoseconds < b.Nanoseconds;
    }

    public static bool operator >(TimeInterval a, TimeInterval b)
    {
        return a.Nanoseconds > b.Nanoseconds;
    }

    public static bool operator <=(TimeInterval a, TimeInterval b)
    {
        return a.Nanoseconds <= b.Nanoseconds;
    }

    public static bool operator >=(TimeInterval a, TimeInterval b)
    {
        return a.Nanoseconds >= b.Nanoseconds;
    }

    public string ToString(CultureInfo cultureInfo, string format = "N4", UnitPresentation? unitPresentation = null)
    {
        return ToString(null, cultureInfo, format, unitPresentation);
    }

    public string ToString(TimeUnit? timeUnit, CultureInfo cultureInfo, string format = "N4", UnitPresentation? unitPresentation = null)
    {
        timeUnit = timeUnit ?? TimeUnit.GetBestTimeUnit(Nanoseconds);
        cultureInfo = cultureInfo ?? DefaultCultureInfo.Instance;
        format = format ?? "N4";
        unitPresentation = unitPresentation ?? UnitPresentation.Default;
        double num = TimeUnit.Convert(Nanoseconds, TimeUnit.Nanosecond, timeUnit);
        if (unitPresentation.IsVisible)
        {
            string text = timeUnit.Name.PadLeft(unitPresentation.MinUnitWidth);
            return num.ToString(format, cultureInfo) + " " + text;
        }

        return num.ToString(format, cultureInfo);
    }

    public override string ToString()
    {
        return ToString(DefaultCultureInfo.Instance);
    }
}
