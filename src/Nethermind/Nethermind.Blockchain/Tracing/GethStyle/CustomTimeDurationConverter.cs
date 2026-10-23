// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Blockchain.Tracing.GethStyle;

internal sealed class TraceDurationFormatException(string message) : FormatException(message);

internal readonly record struct ParsedTraceDuration(TimeSpan? Value, string? Error = null, string? RawText = null);

/// <summary>
/// Deserializes a JSON string as a <see cref="TimeSpan"/> using Go's time.ParseDuration format
/// (e.g. "5s", "100ms", "2m30s"). Serializes back using Go's Duration.String() format.
/// </summary>
public sealed class CustomTimeDurationConverter : JsonConverter<TimeSpan?>
{
    private const ulong NsPerTick = 100;
    private const ulong TicksPerSecond = 10_000_000;
    private const ulong NsPerMicrosecond = 1_000;
    private const ulong NsPerMillisecond = 1_000_000;
    private const ulong NsPerSecond = 1_000_000_000;
    private const ulong SecondsPerMinute = 60;
    private const ulong MinutesPerHour = 60;
    private const ulong NsPerMinute = SecondsPerMinute * NsPerSecond;
    private const ulong NsPerHour = MinutesPerHour * NsPerMinute;

    private const int NsDigitsInMicrosecond = 3;
    private const int NsDigitsInMillisecond = 6;
    private const int NsDigitsInSecond = 9;

    // Matches Go's int64 nanosecond range (positive values must be < MaxNs).
    private const ulong MaxNs = 1UL << 63;

    // Largest possible Format output is "-256204778h59m59.999999999s" = 27 chars.
    private const int FormatBufferSize = 32;

    private const int MaxInputBytes = 64;

    private static readonly SearchValues<char> Digits = SearchValues.Create("0123456789");
    private static readonly SearchValues<char> UnitTerminators = SearchValues.Create(".0123456789");

    // Go scales fractions by repeated *= 10; Math.Pow(10, k) can round differently once k > 22.
    // Inputs are capped at MaxInputBytes, so a fraction never has that many digits.
    private static readonly double[] FractionScales = CreateFractionScales();

    private static double[] CreateFractionScales()
    {
        double[] scales = new double[MaxInputBytes];
        scales[0] = 1;
        for (int i = 1; i < scales.Length; i++) scales[i] = scales[i - 1] * 10;
        return scales;
    }

    public override TimeSpan? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected string duration or null.");

        ParsedTraceDuration parsed = ReadDuration(ref reader);
        return parsed.Error is null ? parsed.Value : throw new JsonException(parsed.Error, new TraceDurationFormatException(parsed.Error));
    }

    internal static ParsedTraceDuration ReadDuration(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected string duration or null.");

        ValidateLength(ref reader);
        Span<char> buffer = stackalloc char[MaxInputBytes];
        return Parse(buffer[..reader.CopyString(buffer)]);
    }

    private static void ValidateLength(ref Utf8JsonReader reader)
    {
        long byteLen = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (byteLen > MaxInputBytes) throw new JsonException($"Duration string exceeds {MaxInputBytes} bytes.");
    }

    internal static ParsedTraceDuration Parse(string value) => Encoding.UTF8.GetByteCount(value) > MaxInputBytes
        ? new(null, $"Duration string exceeds {MaxInputBytes} bytes.")
        : Parse(value.AsSpan());

    private static ParsedTraceDuration Parse(ReadOnlySpan<char> value)
    {
        if (TryParseGoDuration(value, out TimeSpan result) || TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out result)) return new(result);
        return new(null, $"Cannot parse '{value}' as a duration. Expected Go format ('5s', '100ms', '2m30s') or TimeSpan format ('hh:mm:ss').", new string(value));
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else WriteFormatted(writer, value.Value);
    }

    internal static void WriteFormatted(Utf8JsonWriter writer, TimeSpan value)
    {
        Span<char> buf = stackalloc char[FormatBufferSize];
        writer.WriteStringValue(Format(value, buf));
    }

    // Port of Go's Duration.String() — builds the result right-to-left into buf.
    private static ReadOnlySpan<char> Format(TimeSpan value, Span<char> buf)
    {
        long ticks = value.Ticks;
        if (ticks == 0) return "0s";

        bool neg = ticks < 0;
        // `0UL - (ulong)ticks` yields the magnitude even when ticks == long.MinValue.
        ulong absTicks = neg ? unchecked(0UL - (ulong)ticks) : (ulong)ticks;

        // Split before scaling — `absTicks * NsPerTick` wraps once absTicks exceeds ulong.MaxValue / 100
        // (~583 years), which TimeSpan.MaxValue does by ~50×.
        ulong seconds = absTicks / TicksPerSecond;
        ulong nsFraction = (absTicks % TicksPerSecond) * NsPerTick;

        int w = buf.Length;

        if (seconds == 0)
        {
            ulong u = nsFraction;
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
            (w, _) = WriteFrac(buf, w, nsFraction, NsDigitsInSecond);
            w = WriteInt(buf, w, seconds % SecondsPerMinute);
            ulong minutes = seconds / SecondsPerMinute;

            if (minutes > 0)
            {
                buf[--w] = 'm';
                w = WriteInt(buf, w, minutes % MinutesPerHour);
                ulong hours = minutes / MinutesPerHour;

                if (hours > 0)
                {
                    buf[--w] = 'h';
                    w = WriteInt(buf, w, hours);
                }
            }
        }

        if (neg) buf[--w] = '-';

        return buf[w..];
    }

    private static (int w, ulong v) WriteFrac(Span<char> buf, int w, ulong v, int precision)
    {
        // Trailing zeros aren't emitted (matches Go).
        while (precision > 0 && v % 10 == 0)
        {
            v /= 10;
            precision--;
        }

        if (precision == 0) return (w, v);

        for (int i = 0; i < precision; i++)
        {
            buf[--w] = (char)('0' + v % 10);
            v /= 10;
        }
        buf[--w] = '.';
        return (w, v);
    }

    private static int WriteInt(Span<char> buf, int w, ulong v)
    {
        do
        {
            buf[--w] = (char)('0' + v % 10);
            v /= 10;
        } while (v > 0);
        return w;
    }

    // Port of Go's time.ParseDuration — accumulates nanoseconds, then converts to ticks.
    private static bool TryParseGoDuration(ReadOnlySpan<char> s, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        if (s.IsEmpty) return false;

        bool neg = s[0] == '-';
        if (neg || s[0] == '+') s = s[1..];
        if (s.IsEmpty) return false;

        // "0" / "-0" / "+0" are valid without a unit (matches Go).
        if (s is "0") return true;

        ulong totalNs = 0;
        while (!s.IsEmpty)
        {
            if (!TryParseSegment(ref s, out ulong segmentNs)) return false;
            // Check before adding — `ulong` addition would wrap silently past MaxNs.
            if (segmentNs > MaxNs - totalNs) return false;
            totalNs += segmentNs;
        }

        // Go's positive cap is `d > 1<<63 - 1`.
        if (!neg && totalNs > MaxNs - 1) return false;

        long ticks = (long)(totalNs / NsPerTick);
        result = TimeSpan.FromTicks(neg ? -ticks : ticks);
        return true;
    }

    private static bool TryParseSegment(ref ReadOnlySpan<char> s, out ulong ns)
    {
        ns = 0;

        if (!TryTakeInteger(ref s, out ulong v, out bool hasInt)) return false;

        ulong f = 0;
        double scale = 1;
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
            // Preserve Go's floating-point evaluation order before truncating to nanoseconds.
            ns += (ulong)((double)f * ((double)unitNs / scale));
            if (ns > MaxNs) return false;
        }
        return true;
    }

    private static bool TryTakeInteger(ref ReadOnlySpan<char> s, out ulong v, out bool hasInt)
    {
        ReadOnlySpan<char> digits = TakeDigits(ref s);
        hasInt = !digits.IsEmpty;
        if (!hasInt) { v = 0; return true; }
        return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out v) && v <= MaxNs;
    }

    private static void TakeFraction(ref ReadOnlySpan<char> s, out ulong f, out double scale, out bool hasFrac)
    {
        ReadOnlySpan<char> digits = TakeDigits(ref s);
        hasFrac = !digits.IsEmpty;
        f = 0;
        scale = 1;
        int leadingZeros = digits.IndexOfAnyExcept('0');
        if (leadingZeros < 0) return;

        // Go's leadingFraction stops accumulating once the value would exceed 1<<63:
        // 18 significant digits always fit, a 19th only if the result stays <= 1<<63.
        ReadOnlySpan<char> significant = digits[leadingZeros..];
        int taken = Math.Min(significant.Length, 19);
        ulong.TryParse(significant[..taken], NumberStyles.None, CultureInfo.InvariantCulture, out f); // <= 19 ASCII digits always fit in ulong
        if (f > MaxNs)
        {
            f /= 10;
            taken--;
        }

        scale = FractionScales[leadingZeros + taken];
    }

    private static ReadOnlySpan<char> TakeDigits(ref ReadOnlySpan<char> s)
    {
        int end = s.IndexOfAnyExcept(Digits);
        if (end < 0) end = s.Length;
        ReadOnlySpan<char> taken = s[..end];
        s = s[end..];
        return taken;
    }

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

    // Mirrors Go's unitMap in time/format.go. Returns 0 for unknown units.
    private static ulong GetUnitNs(ReadOnlySpan<char> unit) => unit switch
    {
        "ns" => 1,
        "us" or "µs" or "μs" => NsPerMicrosecond, // U+00B5 micro sign; U+03BC Greek mu
        "ms" => NsPerMillisecond,
        "s" => NsPerSecond,
        "m" => NsPerMinute,
        "h" => NsPerHour,
        _ => 0,
    };
}
