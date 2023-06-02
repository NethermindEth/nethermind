// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Tools.Kute.MetricsConsumer;

public class JsonMetricsConsumer : IMetricsConsumer
{
    public void ConsumeMetrics(Metrics metrics)
    {
        var metricsObject = new
        {
            metrics.TotalRunningTime,
            Messages = new
            {
                metrics.Failed,
                Successes = new
                {
                    metrics.Responses,
                    Requests = new
                    {
                        Ignored = metrics.IgnoredRequests, Processed = metrics.ProcessedRequests
                    }
                }
            }
        };

        string json = JsonSerializer.Serialize(
            metricsObject,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new TimeSpanInMsConverter() }
            }
        );
        Console.WriteLine(json);
    }

    private class TimeSpanInMsConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            TimeSpan.FromMilliseconds(double.Parse(reader.GetString()!));

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue($"{value.TotalMilliseconds} ms");
    }
}
