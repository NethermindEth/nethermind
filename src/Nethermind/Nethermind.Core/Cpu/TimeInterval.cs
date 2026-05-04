// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

using System.Globalization;

namespace Nethermind.Core.Cpu;

public readonly struct TimeInterval(double nanoseconds)
{
    public static readonly TimeInterval Nanosecond = TimeUnit.Nanosecond.ToInterval(1L);

    public static readonly TimeInterval Microsecond = TimeUnit.Microsecond.ToInterval(1L);

    public static readonly TimeInterval Millisecond = TimeUnit.Millisecond.ToInterval(1L);

    public static readonly TimeInterval Second = TimeUnit.Second.ToInterval(1L);

    public static readonly TimeInterval Minute = TimeUnit.Minute.ToInterval(1L);

    public static readonly TimeInterval Hour = TimeUnit.Hour.ToInterval(1L);

    public static readonly TimeInterval Day = TimeUnit.Day.ToInterval(1L);

    public double Nanoseconds { get; } = nanoseconds;

    public TimeInterval(double value, TimeUnit unit)
        : this(value * (double)unit.NanosecondAmount)
    {
    }

    public Frequency ToFrequency() => new(Second / this);

    public double ToNanoseconds() => this / Nanosecond;

    public double ToMicroseconds() => this / Microsecond;

    public double ToMilliseconds() => this / Millisecond;

    public double ToSeconds() => this / Second;

    public double ToMinutes() => this / Minute;

    public double ToHours() => this / Hour;

    public double ToDays() => this / Day;

    public static TimeInterval FromNanoseconds(double value) => Nanosecond * value;

    public static TimeInterval FromMicroseconds(double value) => Microsecond * value;

    public static TimeInterval FromMilliseconds(double value) => Millisecond * value;

    public static TimeInterval FromSeconds(double value) => Second * value;

    public static TimeInterval FromMinutes(double value) => Minute * value;

    public static TimeInterval FromHours(double value) => Hour * value;

    public static TimeInterval FromDays(double value) => Day * value;

    public static double operator /(TimeInterval a, TimeInterval b) => 1.0 * a.Nanoseconds / b.Nanoseconds;

    public static TimeInterval operator /(TimeInterval a, double k) => new(a.Nanoseconds / k);

    public static TimeInterval operator /(TimeInterval a, int k) => new(a.Nanoseconds / (double)k);

    public static TimeInterval operator *(TimeInterval a, double k) => new(a.Nanoseconds * k);

    public static TimeInterval operator *(TimeInterval a, int k) => new(a.Nanoseconds * (double)k);

    public static TimeInterval operator *(double k, TimeInterval a) => new(a.Nanoseconds * k);

    public static TimeInterval operator *(int k, TimeInterval a) => new(a.Nanoseconds * (double)k);

    public static bool operator <(TimeInterval a, TimeInterval b) => a.Nanoseconds < b.Nanoseconds;

    public static bool operator >(TimeInterval a, TimeInterval b) => a.Nanoseconds > b.Nanoseconds;

    public static bool operator <=(TimeInterval a, TimeInterval b) => a.Nanoseconds <= b.Nanoseconds;

    public static bool operator >=(TimeInterval a, TimeInterval b) => a.Nanoseconds >= b.Nanoseconds;

    public string ToString(CultureInfo cultureInfo, string format = "N4", UnitPresentation? unitPresentation = null) => ToString(null, cultureInfo, format, unitPresentation);

    public string ToString(TimeUnit? timeUnit, CultureInfo cultureInfo, string format = "N4", UnitPresentation? unitPresentation = null)
    {
        timeUnit ??= TimeUnit.GetBestTimeUnit(Nanoseconds);
        cultureInfo ??= DefaultCultureInfo.Instance;
        format ??= "N4";
        unitPresentation ??= UnitPresentation.Default;
        double num = TimeUnit.Convert(Nanoseconds, TimeUnit.Nanosecond, timeUnit);
        if (unitPresentation.IsVisible)
        {
            string text = timeUnit.Name.PadLeft(unitPresentation.MinUnitWidth);
            return num.ToString(format, cultureInfo) + " " + text;
        }

        return num.ToString(format, cultureInfo);
    }

    public override string ToString() => ToString(DefaultCultureInfo.Instance);
}
