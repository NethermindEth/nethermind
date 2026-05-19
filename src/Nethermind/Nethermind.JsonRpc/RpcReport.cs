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
            ResponseWriteMicroseconds != 0;

        [JsonIgnore]
        public long BoundaryMicroseconds => PreMethodMicroseconds + PostMethodMicroseconds + ResponseWriteMicroseconds;

        public RpcBoundaryTimings WithResponseWrite(long responseWriteMicroseconds) =>
            this with { ResponseWriteMicroseconds = responseWriteMicroseconds, IsMeasured = true };

        public static RpcBoundaryTimings PreMethodOnly(long boundaryStartTimestamp, long boundaryEndTimestamp) =>
            new(GetElapsedMicroseconds(boundaryStartTimestamp, boundaryEndTimestamp), 0, 0, 0) { IsMeasured = true };

        public static RpcBoundaryTimings FromTimestamps(
            long boundaryStartTimestamp,
            long methodStartTimestamp,
            long methodEndTimestamp,
            long responseReadyTimestamp) =>
            new(
                GetElapsedMicroseconds(boundaryStartTimestamp, methodStartTimestamp),
                GetElapsedMicroseconds(methodStartTimestamp, methodEndTimestamp),
                GetElapsedMicroseconds(methodEndTimestamp, responseReadyTimestamp),
                0)
            {
                IsMeasured = true
            };

        private static long GetElapsedMicroseconds(long startTimestamp, long endTimestamp) =>
            (long)Stopwatch.GetElapsedTime(startTimestamp, endTimestamp).TotalMicroseconds;
    }
}
