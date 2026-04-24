// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>
/// Deserializes a JSON string as a <see cref="TimeSpan"/> using Go's time.ParseDuration format
/// (e.g. "5s", "100ms", "2m30s"). Serializes back using Go's Duration.String() format.
/// </summary>
public sealed class CustomTimeDurationConverter : JsonConverter<TimeSpan?>
{
    private const ulong NsPerTick = 100;
    private const ulong NsPerMicrosecond = 1_000;
    private const ulong NsPerMillisecond = 1_000_000;
    private const ulong NsPerSecond = 1_000_000_000;
    private const ulong SecondsPerMinute = 60;
    private const ulong MinutesPerHour = 60;
    private const ulong NsPerMinute = SecondsPerMinute * NsPerSecond;
    private const ulong NsPerHour = MinutesPerHour * NsPerMinute;

    // Number of decimal ns digits per unit — used as precision when emitting fractions.
    private const int NsDigitsInMicrosecond = 3; // Log10(NsPerMicrosecond)
    private const int NsDigitsInMillisecond = 6; // Log10(NsPerMillisecond)
    private const int NsDigitsInSecond = 9;      // Log10(NsPerSecond)

    // Positive values cap at 1<<63 ns, matching Go's int64 nanosecond range.
    private const ulong MaxNs = 1UL << 63;

    // 18 decimal digits always fit in a ulong (10^18 - 1 < 2^63). Extra fractional digits are ignored.
    private const int MaxFractionalDigits = 18;

    // Max output of Format: "-2540400h10m10.000000000s" = 25 chars. 32 is safe.
    private const int FormatBufferSize = 32;

    // Enough to cover any sensible duration literal without falling back to GetString().
    private const int ReadStackBufferSize = 64;

    private static readonly SearchValues<char> Digits = SearchValues.Create("0123456789");

    // A unit token ends at the first '.' or digit — the start of the next segment.
    private static readonly SearchValues<char> UnitTerminators = SearchValues.Create(".0123456789");

    // Powers of 10 up to 10^18 — covers WriteFrac precision (≤9) and fractional-digit capping (≤18).
    private static ReadOnlySpan<ulong> Pow10 =>
    [
        1, 10, 100, 1_000, 10_000,
        100_000, 1_000_000, 10_000_000, 100_000_000, 1_000_000_000,
        10_000_000_000, 100_000_000_000, 1_000_000_000_000, 10_000_000_000_000,
        100_000_000_000_000, 1_000_000_000_000_000, 10_000_000_000_000_000,
        100_000_000_000_000_000, 1_000_000_000_000_000_000,
    ];

    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        // UTF-8 byte count is an upper bound on the UTF-16 char count. Short inputs go through
        // a stack buffer; anything longer falls back to a heap string.
        int byteLen = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
        return byteLen <= ReadStackBufferSize
            ? ReadFromStackBuffer(ref reader)
            : ParseOrThrow(reader.GetString()!);
    }

    // Kept separate so the stack reservation for the buffer only happens on this code path.
    private static TimeSpan ReadFromStackBuffer(ref Utf8JsonReader reader)
    {
        Span<char> stackBuf = stackalloc char[ReadStackBufferSize];
        return ParseOrThrow(stackBuf[..reader.CopyString(stackBuf)]);
    }

    private static TimeSpan ParseOrThrow(ReadOnlySpan<char> s)
    {
        if (TryParseGoDuration(s, out TimeSpan result) || TimeSpan.TryParse(s, out result)) return result;
        throw new JsonException($"Cannot parse '{s}' as a duration. Expected Go format ('5s', '100ms', '2m30s') or TimeSpan format ('hh:mm:ss').");
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else WriteFormatted(writer, value.Value);
    }

    // Kept separate so the stack reservation for the format buffer only happens when we format.
    private static void WriteFormatted(Utf8JsonWriter writer, TimeSpan value)
    {
        Span<char> buf = stackalloc char[FormatBufferSize];
        writer.WriteStringValue(Format(value, buf));
    }

    // Port of Go's Duration.String() (time/time.go). Builds the string right-to-left
    // into the caller's buffer, using the smallest appropriate unit.
    private static ReadOnlySpan<char> Format(TimeSpan value, Span<char> buf)
    {
        long ticks = value.Ticks;
        if (ticks == 0) return "0s";

        bool neg = ticks < 0;
        ulong u = (ulong)(neg ? -ticks : ticks) * NsPerTick;

        int w = buf.Length; // write cursor, fills right-to-left

        if (u < NsPerSecond)
        {
            // Sub-second: pick the smallest unit that fits and emit fractional digits.
            (char unitPrefix, int precision) = u switch
            {
                < NsPerMicrosecond => ('n', 0),
                < NsPerMillisecond => ('µ', NsDigitsInMicrosecond),
                _ => ('m', NsDigitsInMillisecond),
            };

            buf[--w] = 's';
            buf[--w] = unitPrefix;
            (w, u) = WriteFrac(buf, w, u, precision);
            w = WriteInt(buf, w, u);
        }
        else
        {
            buf[--w] = 's';
            (w, u) = WriteFrac(buf, w, u, NsDigitsInSecond);
            w = WriteInt(buf, w, u % SecondsPerMinute);
            u /= SecondsPerMinute;

            if (u > 0)
            {
                buf[--w] = 'm';
                w = WriteInt(buf, w, u % MinutesPerHour);
                u /= MinutesPerHour;

                if (u > 0)
                {
                    buf[--w] = 'h';
                    w = WriteInt(buf, w, u);
                }
            }
        }

        if (neg) buf[--w] = '-';

        return buf[w..];
    }

    // Writes the fractional part of v/10^precision into buf right-to-left, omitting trailing
    // zeros (and the decimal point when the fraction is zero). Returns updated w and v/10^precision.
    private static (int w, ulong v) WriteFrac(Span<char> buf, int w, ulong v, int precision)
    {
        // Drop trailing zeros — Go's format doesn't print them.
        while (precision > 0 && v % 10 == 0)
        {
            v /= 10;
            precision--;
        }

        if (precision == 0) return (w, v); // no fractional part to emit

        for (int i = 0; i < precision; i++)
        {
            buf[--w] = (char)('0' + v % 10);
            v /= 10;
        }
        buf[--w] = '.';
        return (w, v);
    }

    // Writes v into buf right-to-left, returns updated w.
    private static int WriteInt(Span<char> buf, int w, ulong v)
    {
        do
        {
            buf[--w] = (char)('0' + v % 10);
            v /= 10;
        } while (v > 0);
        return w;
    }

    // Port of Go's time.ParseDuration (time/format.go). Accumulates nanoseconds as ulong,
    // then converts to ticks at the end.
    private static bool TryParseGoDuration(ReadOnlySpan<char> s, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        // Special case: "0" is valid without a unit (matches Go).
        if (s is "0") return true;
        if (s.IsEmpty) return false;

        bool neg = s[0] == '-';
        if (neg || s[0] == '+') s = s[1..];
        if (s.IsEmpty) return false;

        ulong totalNs = 0;
        while (!s.IsEmpty)
        {
            if (!TryParseSegment(ref s, out ulong segmentNs)) return false;
            totalNs += segmentNs;
            if (totalNs > MaxNs) return false;
        }

        // Positive values cannot equal 1<<63 (Go's d > 1<<63-1).
        if (!neg && totalNs > MaxNs - 1) return false;

        long ticks = (long)(totalNs / NsPerTick);
        result = TimeSpan.FromTicks(neg ? -ticks : ticks);
        return true;
    }

    private static bool TryParseSegment(ref ReadOnlySpan<char> s, out ulong ns)
    {
        ns = 0;

        // Integer part: [0-9]*.
        if (!TryTakeInteger(ref s, out ulong v, out bool hasInt)) return false;

        // Fractional part: (\.[0-9]*)?.
        ulong f = 0;
        ulong scale = 1;
        bool hasFrac = false;
        if (!s.IsEmpty && s[0] == '.')
        {
            s = s[1..];
            TakeFraction(ref s, out f, out scale, out hasFrac);
        }

        if (!hasInt && !hasFrac) return false;

        if (!TryTakeUnit(ref s, out ulong unitNs)) return false;

        if (v > MaxNs / unitNs) return false;
        ns = v * unitNs;

        if (f > 0)
        {
            // 128-bit multiply keeps the fraction exact; Go uses float64 here and notes
            // it must be nanosecond-accurate for fractions of an hour.
            ns += (ulong)((UInt128)f * unitNs / scale);
            if (ns > MaxNs) return false;
        }
        return true;
    }

    private static bool TryTakeInteger(ref ReadOnlySpan<char> s, out ulong v, out bool hasInt)
    {
        ReadOnlySpan<char> digits = TakeDigits(ref s);
        hasInt = !digits.IsEmpty;
        if (!hasInt) { v = 0; return true; }
        return ulong.TryParse(digits, out v) && v <= MaxNs;
    }

    private static void TakeFraction(ref ReadOnlySpan<char> s, out ulong f, out ulong scale, out bool hasFrac)
    {
        ReadOnlySpan<char> digits = TakeDigits(ref s);
        hasFrac = !digits.IsEmpty;
        if (!hasFrac)
        {
            f = 0;
            scale = 1;
            return;
        }

        // Cap at 18 digits (always fits ulong). Extra precision is dropped — Go would keep
        // up to ~19 digits depending on value, but the difference is below the tick boundary.
        int take = Math.Min(digits.Length, MaxFractionalDigits);
        bool ok = ulong.TryParse(digits[..take], out f);
        Debug.Assert(ok, "digits are ASCII 0-9 from TakeDigits and fit in 18 chars");
        scale = Pow10[take];
    }

    private static ReadOnlySpan<char> TakeDigits(ref ReadOnlySpan<char> s)
    {
        int end = s.IndexOfAnyExcept(Digits);
        if (end < 0) end = s.Length;
        ReadOnlySpan<char> taken = s[..end];
        s = s[end..];
        return taken;
    }

    // Consumes a unit token (everything up to the next digit or '.') and resolves it to
    // nanoseconds. Returns false if the unit is missing or unknown.
    private static bool TryTakeUnit(ref ReadOnlySpan<char> s, out ulong unitNs)
    {
        int end = s.IndexOfAny(UnitTerminators);
        if (end < 0) end = s.Length;
        if (end == 0)
        {
            unitNs = 0;
            return false;
        }
        unitNs = GetUnitNs(s[..end]);
        s = s[end..];
        return unitNs != 0;
    }

    // Nanoseconds per unit — mirrors Go's unitMap in time/format.go. Returns 0 for unknown units.
    private static ulong GetUnitNs(ReadOnlySpan<char> unit) => unit switch
    {
        "ns" => 1,
        "us" or "µs" or "μs" => NsPerMicrosecond, // µ = U+00B5 micro sign; μ = U+03BC Greek letter mu
        "ms" => NsPerMillisecond,
        "s" => NsPerSecond,
        "m" => NsPerMinute,
        "h" => NsPerHour,
        _ => 0,
    };
}
