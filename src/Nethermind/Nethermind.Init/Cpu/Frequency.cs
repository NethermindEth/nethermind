// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

using System.Globalization;

namespace Nethermind.Init.Cpu;

internal readonly struct Frequency
{
    public static readonly Frequency Zero = new Frequency(0.0);

    public static readonly Frequency Hz = FrequencyUnit.Hz.ToFrequency(1L);

    public static readonly Frequency KHz = FrequencyUnit.KHz.ToFrequency(1L);

    public static readonly Frequency MHz = FrequencyUnit.MHz.ToFrequency(1L);

    public static readonly Frequency GHz = FrequencyUnit.GHz.ToFrequency(1L);

    public double Hertz { get; }

    public Frequency(double hertz)
    {
        Hertz = hertz;
    }

    public Frequency(double value, FrequencyUnit unit)
        : this(value * (double)unit.HertzAmount)
    {
    }

    public TimeInterval ToResolution()
    {
        return TimeInterval.Second / Hertz;
    }

    public double ToHz()
    {
        return this / Hz;
    }

    public double ToKHz()
    {
        return this / KHz;
    }

    public double ToMHz()
    {
        return this / MHz;
    }

    public double ToGHz()
    {
        return this / GHz;
    }

    public static Frequency FromHz(double value)
    {
        return Hz * value;
    }

    public static Frequency FromKHz(double value)
    {
        return KHz * value;
    }

    public static Frequency FromMHz(double value)
    {
        return MHz * value;
    }

    public static Frequency FromGHz(double value)
    {
        return GHz * value;
    }

    public static implicit operator Frequency(double value)
    {
        return new Frequency(value);
    }

    public static implicit operator double(Frequency property)
    {
        return property.Hertz;
    }

    public static double operator /(Frequency a, Frequency b)
    {
        return 1.0 * a.Hertz / b.Hertz;
    }

    public static Frequency operator /(Frequency a, double k)
    {
        return new Frequency(a.Hertz / k);
    }

    public static Frequency operator /(Frequency a, int k)
    {
        return new Frequency(a.Hertz / (double)k);
    }

    public static Frequency operator *(Frequency a, double k)
    {
        return new Frequency(a.Hertz * k);
    }

    public static Frequency operator *(Frequency a, int k)
    {
        return new Frequency(a.Hertz * (double)k);
    }

    public static Frequency operator *(double k, Frequency a)
    {
        return new Frequency(a.Hertz * k);
    }

    public static Frequency operator *(int k, Frequency a)
    {
        return new Frequency(a.Hertz * (double)k);
    }

    public static bool TryParse(string s, FrequencyUnit unit, out Frequency freq)
    {
        bool result2 = double.TryParse(s, NumberStyles.Any, DefaultCultureInfo.Instance, out double result);
        freq = new Frequency(result, unit);
        return result2;
    }

    public static bool TryParseHz(string s, out Frequency freq)
    {
        return TryParse(s, FrequencyUnit.Hz, out freq);
    }

    public static bool TryParseKHz(string s, out Frequency freq)
    {
        return TryParse(s, FrequencyUnit.KHz, out freq);
    }

    public static bool TryParseMHz(string s, out Frequency freq)
    {
        return TryParse(s, FrequencyUnit.MHz, out freq);
    }

    public static bool TryParseGHz(string s, out Frequency freq)
    {
        return TryParse(s, FrequencyUnit.GHz, out freq);
    }

    public override string ToString()
    {
        return Hertz + " " + FrequencyUnit.Hz.Name;
    }
}
