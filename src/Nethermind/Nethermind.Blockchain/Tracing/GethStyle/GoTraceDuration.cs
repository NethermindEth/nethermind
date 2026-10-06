// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal static class GoTraceDuration
{
    // Mirrors time.ParseDuration's signed nanosecond range; sub-tick durations remain valid and expire immediately.
    internal static TimeSpan Parse(string text)
    {
        ReadOnlySpan<char> value = text.AsSpan();
        bool negative = value.Length != 0 && value[0] == '-';
        if (value.Length != 0 && value[0] is '+' or '-') value = value[1..];
        if (value.SequenceEqual("0")) return TimeSpan.Zero;
        if (value.IsEmpty) throw Invalid(text);
        ulong total = 0;
        const ulong limit = 1UL << 63;
        while (!value.IsEmpty)
        {
            if (value[0] != '.' && !char.IsAsciiDigit(value[0])) throw Invalid(text);
            ulong integer = 0;
            bool digits = false;
            while (!value.IsEmpty && char.IsAsciiDigit(value[0]))
            {
                uint digit = (uint)(value[0] - '0');
                if (integer > (limit - digit) / 10) throw Invalid(text);
                integer = integer * 10 + digit;
                digits = true;
                value = value[1..];
            }
            ulong fraction = 0;
            double scale = 1;
            bool fractionDigits = false, overflow = false;
            if (!value.IsEmpty && value[0] == '.')
            {
                value = value[1..];
                while (!value.IsEmpty && char.IsAsciiDigit(value[0]))
                {
                    uint digit = (uint)(value[0] - '0');
                    if (!overflow && fraction <= (limit - digit) / 10)
                    {
                        fraction = fraction * 10 + digit;
                        scale *= 10;
                    }
                    else overflow = true;
                    fractionDigits = true;
                    value = value[1..];
                }
            }
            if (!digits && !fractionDigits) throw Invalid(text);
            int end = 0;
            while (end < value.Length && value[end] != '.' && !char.IsAsciiDigit(value[end])) end++;
            if (end == 0) throw new FormatException($"time: missing unit in duration {Quote(text)}");
            ReadOnlySpan<char> unitName = value[..end];
            ulong unit = unitName switch
            {
                "ns" => 1,
                "us" or "µs" or "μs" => 1000,
                "ms" => 1000000,
                "s" => 1000000000,
                "m" => 60000000000,
                "h" => 3600000000000,
                _ => throw new FormatException($"time: unknown unit {Quote(unitName.ToString())} in duration {Quote(text)}")
            };
            value = value[end..];
            if (integer > limit / unit) throw Invalid(text);
            ulong component = integer * unit;
            ulong fractional = (ulong)(fraction * ((double)unit / scale));
            if (component > limit - fractional) throw Invalid(text);
            component += fractional;
            if (total > limit - component) throw Invalid(text);
            total += component;
        }
        if (!negative && total == limit) throw Invalid(text);
        long ticks = (long)(total / 100);
        return TimeSpan.FromTicks(negative ? -ticks : ticks);
    }

    private static string Quote(string value)
    {
        StringBuilder result = new("\"");
        foreach (Rune rune in value.EnumerateRunes())
        {
            string? escape = rune.Value switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\a' => "\\a",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\v' => "\\v",
                _ => null
            };
            if (escape is not null) result.Append(escape);
            else if (rune.Value < 32 || rune.Value == 127) result.Append("\\x").Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture));
            else if (rune.Value == 32 || Rune.IsLetter(rune) || Rune.IsNumber(rune) || Rune.IsPunctuation(rune) || Rune.IsSymbol(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                result.Append(rune.ToString());
            else result.Append(rune.Value < 65536 ? "\\u" : "\\U").Append(rune.Value.ToString(rune.Value < 65536 ? "x4" : "x8", CultureInfo.InvariantCulture));
        }
        return result.Append('"').ToString();
    }
    private static FormatException Invalid(string text) => new($"time: invalid duration {Quote(text)}");
}
