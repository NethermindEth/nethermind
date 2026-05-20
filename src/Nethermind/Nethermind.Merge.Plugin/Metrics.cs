// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Runtime.Serialization;
using Nethermind.Core.Attributes;

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
    }
}
