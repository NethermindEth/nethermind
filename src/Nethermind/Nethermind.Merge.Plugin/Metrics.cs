// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;
using Nethermind.Core.Metric;
using Nethermind.Merge.Plugin.Synchronization;

namespace Nethermind.Merge.Plugin
{
    public static class Metrics
    {
        [GaugeMetric]
        [Description("NewPayload request execution time")]
        public static long NewPayloadExecutionTime { get; set; }

        [GaugeMetric]
        [Description("ForkchoiceUpdated request execution time")]
        public static long ForkchoiceUpdatedExecutionTime { get; set; }

        [CounterMetric]
        [Description("Number of GetPayload Requests")]
        public static long GetPayloadRequests { get; set; }

        [CounterMetric]
        [Description("Number of finalized blocks caught up by applying their block access list instead of executing them")]
        public static long BalCatchUpBlocks { get; set; }

        [CounterMetric]
        [KeyIsLabel("reason")]
        [Description("Number of finalized blocks executed after all because they could not be caught up from their block access list, by reason")]
        public static ConcurrentDictionary<BalCatchUpFallbackReason, long> BalCatchUpFallbacks { get; } = new();

        [GaugeMetric]
        [Description("Number of Transactions included in the Last GetPayload Request")]
        public static int NumberOfTransactionsInGetPayload { get; set; }

        [GaugeMetric]
        [Description("Number of Blobs requested by engine_getBlobsV1")]
        [DataMember(Name = "execution_engine_getblobs_requested_total")]
        public static int NumberOfRequestedBlobs { get; set; }

        [GaugeMetric]
        [Description("Number of Blobs sent by engine_getBlobsV1")]
        [DataMember(Name = "execution_engine_getblobs_available_total")]
        public static int NumberOfSentBlobs { get; set; }

        [GaugeMetric]
        [Description("Number of responses to engine_getBlobsV1 and engine_getBlobsV2 with all requested blobs")]
        [DataMember(Name = "execution_engine_getblobs_hit_total")]
        public static int GetBlobsRequestsSuccessTotal { get; set; }

        [GaugeMetric]
        [Description("Number of responses to engine_getBlobsVX without all requested blobs")]
        [DataMember(Name = "execution_engine_getblobs_miss_total")]
        public static int GetBlobsRequestsFailureTotal { get; set; }

        [CounterMetric]
        [Description("Number of Blobs requested by engine_getBlobsV2")]
        public static int GetBlobsRequestsTotal { get; set; }

        [CounterMetric]
        [Description("Number of Blobs requested by engine_getBlobsV2 that are present in the blobpool")]
        public static int GetBlobsRequestsInBlobpoolTotal { get; set; }

        [GaugeMetric]
        [Description("Time taken to return the blobs from engine_getBlobsV2 request")]
        public static long GetBlobsRequestDurationSeconds { get; set; }

        [CounterMetric]
        [Description("Total number of SSZ-REST engine API requests received")]
        [DataMember(Name = "execution_engine_ssz_rest_requests_total")]
        public static long SszRestRequestsTotal { get; set; }

        [CounterMetric]
        [Description("Total number of SSZ-REST engine API requests that returned a 2xx response")]
        [DataMember(Name = "execution_engine_ssz_rest_requests_success_total")]
        public static long SszRestRequestsSuccessTotal { get; set; }

        [CounterMetric]
        [Description("Total number of SSZ-REST engine API requests that returned a 4xx response (bad request, auth failure, not found, payload too large)")]
        [DataMember(Name = "execution_engine_ssz_rest_requests_client_error_total")]
        public static long SszRestRequestsClientErrorTotal { get; set; }

        [CounterMetric]
        [Description("Total number of SSZ-REST engine API requests that returned a 5xx response")]
        [DataMember(Name = "execution_engine_ssz_rest_requests_server_error_total")]
        public static long SszRestRequestsServerErrorTotal { get; set; }

        [CounterMetric]
        [Description("Total number of SSZ-REST engine API requests whose body could not be decoded (malformed SSZ, truncated data, etc.)")]
        [DataMember(Name = "execution_engine_ssz_rest_decode_failures_total")]
        public static long SszRestDecodeFailuresTotal { get; set; }

        [CounterMetric]
        [Description("Total bytes received in SSZ-REST engine API request bodies")]
        [DataMember(Name = "execution_engine_ssz_rest_request_bytes_total")]
        public static long SszRestRequestBytesTotal { get; set; }

        [CounterMetric]
        [Description("Number of engine keep-alive ticks, by its timer or by an Engine API request")]
        public static long EngineKeepAliveTicks { get; set; }

        [CounterMetric]
        [Description("Number of engine keep-alive ticks asked for by engine_getBlobs or engine_forkchoiceUpdated")]
        public static long EngineKeepAliveSignalledTicks { get; set; }

        [CounterMetric]
        [Description("Number of engine keep-alive CPU spins")]
        public static long EngineKeepAliveCpuTicks { get; set; }

        [CounterMetric]
        [Description("Number of 4 KiB engine keep-alive reads past the page cache")]
        public static long EngineKeepAliveReads { get; set; }

        [CounterMetric]
        [Description("Number of engine keep-alive reads that took more than 1 ms")]
        public static long EngineKeepAliveSlowReads { get; set; }

        [CounterMetric]
        [Description("Number of failed engine keep-alive reads")]
        public static long EngineKeepAliveReadErrors { get; set; }

        [GaugeMetric]
        [Description("Duration of the last engine keep-alive read, in microseconds")]
        public static long EngineKeepAliveLastReadMicros { get; set; }

        [GaugeMetric]
        [Description("Longest engine keep-alive read since start, in microseconds")]
        public static long EngineKeepAliveMaxReadMicros { get; set; }

        [HistogramMetric(Buckets = [20, 50, 100, 200, 500, 1_000, 2_000, 3_000, 5_000, 7_500, 10_000, 20_000, 50_000, 100_000])]
        [Description("Duration of engine keep-alive reads, in microseconds")]
        public static IMetricObserver EngineKeepAliveReadMicros { get; set; } = NoopMetricObserver.Instance;

        [GaugeMetric]
        [Description("How late the engine keep-alive's timer woke its thread on the last timed tick, in microseconds")]
        public static long EngineKeepAliveLastJitterMicros { get; set; }

        [GaugeMetric]
        [Description("Latest the engine keep-alive's timer woke its thread since start, in microseconds")]
        public static long EngineKeepAliveMaxJitterMicros { get; set; }

        [HistogramMetric(Buckets = [10, 25, 50, 100, 250, 500, 1_000, 2_000, 5_000, 10_000, 50_000])]
        [Description("How late the engine keep-alive's timer woke its thread, in microseconds")]
        public static IMetricObserver EngineKeepAliveJitterMicros { get; set; } = NoopMetricObserver.Instance;

        [GaugeMetric]
        [Description("How engine keep-alive reads reach the device: 0 none, 1 O_DIRECT, 2 after dropping the file from the page cache")]
        public static int EngineKeepAliveDiskReadMode { get; set; }
    }
}
