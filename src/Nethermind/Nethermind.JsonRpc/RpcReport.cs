// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc
{
    public readonly record struct RpcReport(string Method, long HandlingTimeMicroseconds, bool Success)
    {
        public const string UnknownMethod = "unknown";
        public static readonly RpcReport Error = new("# error #", 0, false);

        [JsonIgnore]
        public RpcBoundaryTimings BoundaryTimings { get; init; }
    }

    /// <summary>
    /// Captures internal JSON-RPC boundary timing split from RPC method-body time.
    /// </summary>
    public readonly record struct RpcBoundaryTimings(
        long PreMethodMicroseconds,
        long MethodBodyMicroseconds,
        long PostMethodMicroseconds,
        long ResponseWriteMicroseconds)
    {
        [JsonIgnore]
        public bool IsMeasured { get; init; }

        [JsonIgnore]
        public bool HasMeasurements =>
            IsMeasured ||
            PreMethodMicroseconds != 0 ||
            MethodBodyMicroseconds != 0 ||
            PostMethodMicroseconds != 0 ||
            ResponseWriteMicroseconds != 0 ||
            RequestBodyCollectionMicroseconds != 0 ||
            EnvelopeParseMicroseconds != 0 ||
            ResponseFlushMicroseconds != 0 ||
            ResponseFlushCount != 0;

        [JsonIgnore]
        public long BoundaryMicroseconds => PreMethodMicroseconds + PostMethodMicroseconds + ResponseWriteMicroseconds;

        [JsonIgnore]
        public long ResponseFlushMicroseconds { get; init; }

        [JsonIgnore]
        public long ResponseFlushCount { get; init; }

        [JsonIgnore]
        public long RequestBodyCollectionMicroseconds { get; init; }

        [JsonIgnore]
        public long EnvelopeParseMicroseconds { get; init; }

        public RpcBoundaryTimings WithResponseWrite(
            long responseWriteMicroseconds,
            long responseFlushMicroseconds = 0,
            long responseFlushCount = 0) =>
            this with
            {
                ResponseWriteMicroseconds = responseWriteMicroseconds,
                ResponseFlushMicroseconds = responseFlushMicroseconds,
                ResponseFlushCount = responseFlushCount,
                IsMeasured = true
            };

        public static RpcBoundaryTimings PreMethodOnly(
            long boundaryStartTimestamp,
            long boundaryEndTimestamp,
            long requestBodyCollectionMicroseconds = 0,
            long envelopeParseMicroseconds = 0) =>
            new(GetElapsedMicroseconds(boundaryStartTimestamp, boundaryEndTimestamp), 0, 0, 0)
            {
                IsMeasured = true,
                RequestBodyCollectionMicroseconds = requestBodyCollectionMicroseconds,
                EnvelopeParseMicroseconds = envelopeParseMicroseconds
            };

        public static RpcBoundaryTimings FromTimestamps(
            long boundaryStartTimestamp,
            long methodStartTimestamp,
            long methodEndTimestamp,
            long responseReadyTimestamp,
            long requestBodyCollectionMicroseconds = 0,
            long envelopeParseMicroseconds = 0) =>
            new(
                GetElapsedMicroseconds(boundaryStartTimestamp, methodStartTimestamp),
                GetElapsedMicroseconds(methodStartTimestamp, methodEndTimestamp),
                GetElapsedMicroseconds(methodEndTimestamp, responseReadyTimestamp),
                0)
            {
                IsMeasured = true,
                RequestBodyCollectionMicroseconds = requestBodyCollectionMicroseconds,
                EnvelopeParseMicroseconds = envelopeParseMicroseconds
            };

        private static long GetElapsedMicroseconds(long startTimestamp, long endTimestamp) =>
            (long)Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMicroseconds;
    }
}
