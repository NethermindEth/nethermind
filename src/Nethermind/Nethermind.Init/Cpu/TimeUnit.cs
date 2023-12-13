// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/AndreyAkinshin/perfolizer
// Licensed under the MIT License

using System;
using System.Linq;

namespace Nethermind.Init.Cpu;

internal class TimeUnit : IEquatable<TimeUnit>
{
    public static readonly TimeUnit Nanosecond = new TimeUnit("ns", "Nanosecond", 1L);

    public static readonly TimeUnit Microsecond = new TimeUnit("μs", "Microsecond", 1000L);

    public static readonly TimeUnit Millisecond = new TimeUnit("ms", "Millisecond", 1000000L);

    public static readonly TimeUnit Second = new TimeUnit("s", "Second", 1000000000L);

    public static readonly TimeUnit Minute = new TimeUnit("m", "Minute", Second.NanosecondAmount * 60);

    public static readonly TimeUnit Hour = new TimeUnit("h", "Hour", Minute.NanosecondAmount * 60);

    public static readonly TimeUnit Day = new TimeUnit("d", "Day", Hour.NanosecondAmount * 24);

    public static readonly TimeUnit[] All = new TimeUnit[7] { Nanosecond, Microsecond, Millisecond, Second, Minute, Hour, Day };

    public string Name { get; }

    public string Description { get; }

    public long NanosecondAmount { get; }

    private TimeUnit(string name, string description, long nanosecondAmount)
    {
        Name = name;
        Description = description;
        NanosecondAmount = nanosecondAmount;
    }

    public TimeInterval ToInterval(long value = 1L)
    {
        return new TimeInterval(value, this);
    }

    public static TimeUnit GetBestTimeUnit(params double[] values)
    {
        if (values.Length == 0)
        {
            return Nanosecond;
        }

        double num = values.Min();
        TimeUnit[] all = All;
        foreach (TimeUnit timeUnit in all)
        {
            if (num < (double)(timeUnit.NanosecondAmount * 1000))
            {
                return timeUnit;
            }
        }

        return All.Last();
    }

    public static double Convert(double value, TimeUnit from, TimeUnit to)
    {
        return value * (double)from.NanosecondAmount / (double)(to ?? GetBestTimeUnit(value)).NanosecondAmount;
    }

    public bool Equals(TimeUnit? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals((object)this, other))
        {
            return true;
        }

        if (object.Equals(Name, other.Name) && string.Equals(Description, other.Description))
        {
            return NanosecondAmount == other.NanosecondAmount;
        }

        return false;
    }

    public override bool Equals(object? obj)
    {
        if (obj == null)
        {
            return false;
        }

        if ((object)this == obj)
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((TimeUnit)obj);
    }

    public override int GetHashCode()
    {
        return (((((Name != null) ? Name.GetHashCode() : 0) * 397) ^ ((Description != null) ? Description.GetHashCode() : 0)) * 397) ^ NanosecondAmount.GetHashCode();
    }

    public static bool operator ==(TimeUnit left, TimeUnit right)
    {
        return object.Equals(left, right);
    }

    public static bool operator !=(TimeUnit left, TimeUnit right)
    {
        return !object.Equals(left, right);
    }
}
