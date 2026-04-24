// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Deserializes a JSON string as a <see cref="TimeSpan"/> using Go's time.ParseDuration format
/// (e.g. "5s", "100ms", "2m30s"). Serializes back using Go's Duration.String() format.
/// </summary>
public sealed class CustomTimeDurationConverter : JsonConverter<TimeSpan?>
{
    // Nanoseconds per unit — mirrors Go's unitMap in time/format.go.
    private static readonly Dictionary<string, ulong> UnitMap = new()
    {
        ["ns"] = 1,
        ["us"] = 1_000,
        ["µs"] = 1_000, // U+00B5 micro sign
        ["μs"] = 1_000, // U+03BC Greek letter mu
        ["ms"] = 1_000_000,
        ["s"] = 1_000_000_000,
        ["m"] = 60_000_000_000,
        ["h"] = 3_600_000_000_000,
    };

    private const ulong NsPerMicrosecond = 1_000;
    private const ulong NsPerMillisecond = 1_000_000;
    private const ulong NsPerSecond = 1_000_000_000;

    // Overflow threshold mirrors Go's int64 nanosecond cap (1<<63).
    private const ulong MaxNs = 1UL << 63;

    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        string s = reader.GetString()!;
        if (TryParse(s, out TimeSpan result) || TimeSpan.TryParse(s, out result)) return result;
        throw new JsonException($"Cannot parse '{s}' as a duration. Expected Go format ('5s', '100ms', '2m30s') or TimeSpan format ('hh:mm:ss').");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(Format(value.Value));
    }

    // Port of Go's Duration.String() (time/time.go). Builds the string right-to-left
    // in a stack-allocated buffer, using the smallest appropriate unit.
    private static string Format(TimeSpan value)
    {
        long ticks = value.Ticks;
        if (ticks == 0) return "0s";

        bool neg = ticks < 0;
        ulong u = (ulong)(neg ? -ticks : ticks) * 100; // ticks → nanoseconds

        // Max output: "-2540400h10m10.000000000s" = 25 chars; 32 is safe.
        Span<char> buf = stackalloc char[32];
        int w = buf.Length; // write position, moves left

        if (u < NsPerSecond)
        {
            int prec;
            w--; buf[w] = 's';
            w--;
            if (u < NsPerMicrosecond)
            {
                prec = 0;
                buf[w] = 'n';
            }
            else if (u < NsPerMillisecond)
            {
                prec = 3;
                buf[w] = 'µ'; // single char in UTF-16 (U+00B5)
            }
            else
            {
                prec = 6;
                buf[w] = 'm';
            }
            (w, u) = FmtFrac(buf, w, u, prec);
            w = FmtInt(buf, w, u);
        }
        else
        {
            w--; buf[w] = 's';
            (w, u) = FmtFrac(buf, w, u, 9);
            w = FmtInt(buf, w, u % 60);
            u /= 60;

            if (u > 0)
            {
                w--; buf[w] = 'm';
                w = FmtInt(buf, w, u % 60);
                u /= 60;

                if (u > 0)
                {
                    w--; buf[w] = 'h';
                    w = FmtInt(buf, w, u);
                }
            }
        }

        if (neg) { w--; buf[w] = '-'; }

        return new string(buf[w..]);
    }

    // Port of Go's fmtFrac: writes the fractional part of v/10^prec, omitting trailing
    // zeros and the decimal point when the fraction is zero. Returns new w and v/10^prec.
    private static (int w, ulong v) FmtFrac(Span<char> buf, int w, ulong v, int prec)
    {
        bool print = false;
        for (int i = 0; i < prec; i++)
        {
            ulong digit = v % 10;
            print = print || digit != 0;
            if (print) { w--; buf[w] = (char)('0' + digit); }
            v /= 10;
        }
        if (print) { w--; buf[w] = '.'; }
        return (w, v);
    }

    // Port of Go's fmtInt: writes v into buf right-to-left, returns new w.
    private static int FmtInt(Span<char> buf, int w, ulong v)
    {
        if (v == 0) { w--; buf[w] = '0'; }
        else { while (v > 0) { w--; buf[w] = (char)('0' + v % 10); v /= 10; } }
        return w;
    }

    // Port of Go's time.ParseDuration (time/format.go). Accumulates nanoseconds as ulong,
    // then converts to ticks (1 tick = 100 ns) at the end.
    private static bool TryParse(string s, out TimeSpan result)
    {
        result = default;

        // Special case: "0" is valid without a unit (matches Go).
        if (s == "0") return true;
        if (s.Length == 0) return false;

        bool neg = false;
        int i = 0;
        if (s[0] == '-' || s[0] == '+')
        {
            neg = s[0] == '-';
            i++;
        }

        if (i >= s.Length) return false;

        ulong d = 0;
        while (i < s.Length)
        {
            if (s[i] != '.' && (s[i] < '0' || s[i] > '9')) return false;

            // Integer part: consume [0-9]*.
            ulong v = 0;
            bool pre = false;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9')
            {
                if (v > ulong.MaxValue / 10) return false; // overflow
                v = v * 10 + (ulong)(s[i] - '0');
                i++;
                pre = true;
            }

            // Fractional part: consume (\.[0-9]*)?.
            ulong f = 0;
            double scale = 1;
            bool post = false;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9')
                {
                    if (f <= (ulong.MaxValue - 9) / 10) { f = f * 10 + (ulong)(s[i] - '0'); scale *= 10; }
                    i++;
                    post = true;
                }
            }

            if (!pre && !post) return false; // ".s" or similar — no digits at all

            // Unit: consume until next digit or '.'.
            int unitStart = i;
            while (i < s.Length && s[i] != '.' && (s[i] < '0' || s[i] > '9'))
                i++;

            if (i == unitStart) return false; // missing unit

            string unit = s[unitStart..i];
            if (!UnitMap.TryGetValue(unit, out ulong unitNs)) return false;

            if (v > MaxNs / unitNs) return false; // overflow
            v *= unitNs;
            if (f > 0)
            {
                v += (ulong)(f * (unitNs / scale));
                if (v > MaxNs) return false; // overflow after fraction
            }
            d += v;
            if (d > MaxNs) return false; // overflow accumulator
        }

        if (!neg && d > MaxNs - 1) return false; // positive overflow (mirrors Go's d > 1<<63-1)

        long ticks = (long)(d / 100); // 1 tick = 100 ns
        result = TimeSpan.FromTicks(neg ? -ticks : ticks);
        return true;
    }
}
