// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Nethermind.Blockchain.Tracing.GethStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

[TestFixture]
public class CustomTimeDurationConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new CustomTimeDurationConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static TimeSpan? Deserialize(string json) =>
        JsonSerializer.Deserialize<TimeSpan?>(json, Options);

    private static string Serialize(TimeSpan? value) =>
        JsonSerializer.Serialize(value, Options);

    private static IEnumerable<TestCaseData> ValidGoDurationCases() =>
    [
        new TestCaseData("\"0\"", TimeSpan.Zero).SetName("zero"),
        new TestCaseData("\"0s\"", TimeSpan.Zero).SetName("zero_with_unit"),
        new TestCaseData("\"5s\"", TimeSpan.FromSeconds(5)).SetName("seconds"),
        new TestCaseData("\"100ms\"", TimeSpan.FromMilliseconds(100)).SetName("milliseconds"),
        new TestCaseData("\"500us\"", TimeSpan.FromMicroseconds(500)).SetName("microseconds_us"),
        new TestCaseData("\"500µs\"", TimeSpan.FromMicroseconds(500)).SetName("microseconds_micro_sign"),
        new TestCaseData("\"500μs\"", TimeSpan.FromMicroseconds(500)).SetName("microseconds_greek_mu"),
        new TestCaseData("\"200ns\"", TimeSpan.FromTicks(2)).SetName("nanoseconds"),
        new TestCaseData("\"2m30s\"", TimeSpan.FromSeconds(150)).SetName("minutes_and_seconds"),
        new TestCaseData("\"1h30m\"", TimeSpan.FromMinutes(90)).SetName("hours_and_minutes"),
        new TestCaseData("\"1h30m45s\"", new TimeSpan(1, 30, 45)).SetName("hours_minutes_seconds"),
        new TestCaseData("\"1.5s\"", TimeSpan.FromMilliseconds(1500)).SetName("fractional_seconds"),
        new TestCaseData("\"2.5ms\"", TimeSpan.FromTicks(25000)).SetName("fractional_milliseconds"),
        new TestCaseData("\"-5s\"", TimeSpan.FromSeconds(-5)).SetName("negative"),
        new TestCaseData("\"+5s\"", TimeSpan.FromSeconds(5)).SetName("positive_sign"),
        new TestCaseData("null", null).SetName("null"),
    ];

    [TestCaseSource(nameof(ValidGoDurationCases))]
    public void read_go_duration_format_parses_correctly(string json, TimeSpan? expected) =>
        Deserialize(json).Should().Be(expected);

    private static IEnumerable<TestCaseData> ValidCSharpTimeSpanCases() =>
    [
        new TestCaseData("\"00:00:05\"", TimeSpan.FromSeconds(5)).SetName("hh_mm_ss"),
        new TestCaseData("\"01:30:00\"", TimeSpan.FromMinutes(90)).SetName("hours_and_minutes"),
        new TestCaseData("\"00:00:00.100\"", TimeSpan.FromMilliseconds(100)).SetName("with_fractional_seconds"),
        new TestCaseData("\"1.02:03:04\"", new TimeSpan(1, 2, 3, 4)).SetName("days_hours_minutes_seconds"),
    ];

    [TestCaseSource(nameof(ValidCSharpTimeSpanCases))]
    public void read_csharp_timespan_format_parses_correctly(string json, TimeSpan? expected) =>
        Deserialize(json).Should().Be(expected);

    private static IEnumerable<TestCaseData> InvalidDurationCases() =>
    [
        new TestCaseData("\"abc\"").SetName("not_a_duration"),
        new TestCaseData("\"5x\"").SetName("unknown_unit"),
        new TestCaseData("\"\"").SetName("empty_string"),
        new TestCaseData("\"-\"").SetName("sign_only"),
    ];

    [TestCaseSource(nameof(InvalidDurationCases))]
    public void read_invalid_duration_throws(string json)
    {
        Action act = () => Deserialize(json);
        act.Should().Throw<JsonException>();
    }

    private static IEnumerable<TestCaseData> WriteCases() =>
    [
        new TestCaseData((TimeSpan?)TimeSpan.Zero, "\"0s\"").SetName("zero"),
        new TestCaseData((TimeSpan?)TimeSpan.FromSeconds(5), "\"5s\"").SetName("whole_seconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromMilliseconds(100), "\"100ms\"").SetName("whole_milliseconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromMicroseconds(500), "\"500µs\"").SetName("whole_microseconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromTicks(2), "\"200ns\"").SetName("nanoseconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromMilliseconds(1500), "\"1.5s\"").SetName("fractional_seconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromMicroseconds(1500), "\"1.5ms\"").SetName("fractional_milliseconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromMinutes(90), "\"1h30m0s\"").SetName("hours_and_minutes"),
        new TestCaseData((TimeSpan?)new TimeSpan(1, 30, 45), "\"1h30m45s\"").SetName("hours_minutes_seconds"),
        new TestCaseData((TimeSpan?)TimeSpan.FromSeconds(-5), "\"-5s\"").SetName("negative"),
        new TestCaseData((TimeSpan?)null, "null").SetName("null"),
    ];

    [TestCaseSource(nameof(WriteCases))]
    public void write_serializes_as_go_duration_string(TimeSpan? value, string expected) =>
        Serialize(value).Should().Be(expected);

    private static IEnumerable<TestCaseData> RoundTripCases() =>
    [
        new TestCaseData(TimeSpan.FromSeconds(5)).SetName("seconds"),
        new TestCaseData(TimeSpan.FromMilliseconds(100)).SetName("milliseconds"),
        new TestCaseData(TimeSpan.FromMicroseconds(500)).SetName("microseconds"),
        new TestCaseData(TimeSpan.FromTicks(2)).SetName("nanoseconds"),
        new TestCaseData(TimeSpan.FromMilliseconds(1500)).SetName("fractional_seconds"),
        new TestCaseData(new TimeSpan(1, 30, 45)).SetName("hours_minutes_seconds"),
        new TestCaseData(TimeSpan.Zero).SetName("zero"),
    ];

    [TestCaseSource(nameof(RoundTripCases))]
    public void round_trip_serialize_then_deserialize_returns_original(TimeSpan value)
    {
        string json = Serialize(value);
        Deserialize(json).Should().Be(value);
    }
}
