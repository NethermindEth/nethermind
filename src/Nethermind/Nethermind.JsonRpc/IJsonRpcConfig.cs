// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.JsonRpc;

public interface IJsonRpcConfig : IConfig
{
    [ConfigItem(
        Description = "Whether to enable the JSON-RPC service.",
        DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "The JSON-RPC service host.", DefaultValue = "127.0.0.1")]
    string Host { get; set; }

    [ConfigItem(Description = "The request timeout, in milliseconds.", DefaultValue = "20000")]
    int Timeout { get; set; }

    [ConfigItem(
        Description = """
            The max number of concurrent requests waiting in the exclusive (non-sharable) queue for:

            - `eth_getLogs`
            - `eth_newFilter`
            - `eth_newBlockFilter`
            - `eth_newPendingTransactionFilter`
            - `eth_uninstallFilter`

            Calls beyond the limit return HTTP 503 immediately. `0` to lift the limit.
            """,
        DefaultValue = "500")]
    int RequestQueueLimit { get; set; }

    [ConfigItem(
        Description = """
            The max number of concurrent in-flight requests on the shared (sharable) singleton handler.
            Caps heavy methods promoted to sharable — `eth_call`, `eth_estimateGas`,
            `eth_createAccessList` — preventing unbounded concurrency from exhausting memory.
            Light sharable methods (e.g. `eth_blockNumber`, `eth_getBalance`) complete in <1 ms and
            effectively never approach this limit. `0` to lift the limit.
            """,
        DefaultValue = "10000")]
    int MaxConcurrentSharedRequests { get; set; }

    [ConfigItem(
        Description = "The path to the base file for diagnostic recording.",
        DefaultValue = "logs/rpc.{counter}.txt")]
    string RpcRecorderBaseFilePath { get; set; }

    [ConfigItem(Description = "The diagnostic recording mode.", DefaultValue = "None")]
    RpcRecorderState RpcRecorderState { get; set; }

    [ConfigItem(Description = "The JSON-RPC service HTTP port.", DefaultValue = "8545", IsPortOption = true)]
    int Port { get; set; }

    [ConfigItem(Description = "The JSON-RPC service WebSockets port.", DefaultValue = "8545", IsPortOption = true)]
    int WebSocketsPort { get; set; }

    [ConfigItem(Description = "The path to connect a UNIX domain socket over.")]
    string IpcUnixDomainSocketPath { get; set; }

    [ConfigItem(Description = "Whether to set the IPC socket UNIX file permissions to owner-only (600).", DefaultValue = "true")]
    bool RestrictIpcSocketPermissions { get; set; }

    [ConfigItem(
        Description = """
            An array of JSON-RPC namespaces to enable. For instance, `[debug,eth]`.

            Built-in namespaces:

            - `admin`
            - `client`
            - `debug`
            - `engine`
            - `eth`
            - `evm`
            - `health`
            - `net`
            - `parity`
            - `personal`
            - `proof`
            - `rpc`
            - `subscribe`
            - `trace`
            - `txpool`
            - `web3`


            """,
        DefaultValue = "[Eth,Subscribe,Trace,TxPool,Web3,Proof,Net,Parity,Health,Rpc]")]
    string[] EnabledModules { get; set; }

    [ConfigItem(
        Description = "An array of additional JSON-RPC URLs to listen at with protocol and JSON-RPC namespace list. For instance, `[http://localhost:8546|http;ws|eth;web3]`.",
        DefaultValue = "[]")]
    string[] AdditionalRpcUrls { get; set; }

    [ConfigItem(Description = "The maximum gas limit for `eth_call` and `eth_estimateGas`.", DefaultValue = "100000000")]
    ulong? GasCap { get; set; }

    [ConfigItem(
        Description = "The interval, in seconds, between the JSON-RPC stats report log.",
        DefaultValue = "300")]
    int ReportIntervalSeconds { get; set; }

    [ConfigItem(
        Description = "Whether to buffer responses before sending them. This allows using of `Content-Length` instead of `Transfer-Encoding: chunked`. Note that it may degrade performance on large responses. The max buffered response length is 2GB. Chunked responses can be larger.",
        DefaultValue = "false")]
    bool BufferResponses { get; set; }

    [ConfigItem(
        Description = "The path to a file with the list of new-line-separated JSON-RPC calls. If specified, only the calls from that file are allowed.",
        DefaultValue = "Data/jsonrpc.filter")]
    string CallsFilterFilePath { get; set; }

    [ConfigItem(Description = "The max length of HTTP request body, in bytes.", DefaultValue = "30000000")]
    long? MaxRequestBodySize { get; set; }

    [ConfigItem(
        Description = "The max number of logs per response for the `eth_getLogs` JSON-RPC method. `0` to lift the limit.",
        DefaultValue = "20000")]
    public int MaxLogsPerResponse { get; set; }

    [ConfigItem(
        Description = "Whether to stream `debug_trace*` and `trace_*` responses as the EVM executes (lower TTFB and bounded memory). For `debug_trace*` can be overridden per-call via `GethTraceOptions.StreamMode`.",
        DefaultValue = "true")]
    public bool EnableTracingStreamMode { get; set; }

    [ConfigItem(
        Description = "Whether to stream `eth_getLogs` and `eth_getFilterLogs` responses as logs are found. When enabled, unauthenticated responses stop at `MaxLogsPerResponse` or `MaxLogsResponseBodySize` instead of buffering the full result and returning a limit error.",
        DefaultValue = "false")]
    public bool EnableLogsStreamMode { get; set; }

    [ConfigItem(
        Description = "The max response body size, in bytes, for streamed `eth_getLogs` and `eth_getFilterLogs` JSON-RPC responses. Ignored unless `EnableLogsStreamMode` is enabled. `null` to use `MaxBatchResponseBodySize`.",
        DefaultValue = "null")]
    public long? MaxLogsResponseBodySize { get; set; }

    [ConfigItem(
        Description = "The number of concurrent instances of the Debug RPC module (`debug_trace*`, `debug_getRawBlock`, etc.). Calls beyond this cap return `LimitExceeded`. Defaults to the number of logical processors.")]
    public int? DebugModuleConcurrentInstances { get; set; }

    [ConfigItem(
        Description = """
            The number of concurrent instances for non-sharable calls:

            - `eth_getLogs`
            - `eth_newBlockFilter`
            - `eth_newFilter`
            - `eth_newPendingTransactionFilter`
            - `eth_uninstallFilter`

            This limits the load on the CPU and I/O to reasonable levels. If the limit is exceeded,
            HTTP 503 is returned along with the JSON-RPC error. Also acts as the hard active
            concurrency cap on the override-path env pool used by sharable `eth_call` /
            `eth_estimateGas` / `eth_createAccessList` when called with state or blob-base-fee
            overrides: calls beyond this cap fail with a `LimitExceeded` JSON-RPC error. Defaults
            to the number of logical processors.
            """)]
    int? EthModuleConcurrentInstances { get; set; }

    [ConfigItem(
        Description = """
            The number of EVM-executing JSON-RPC requests (`eth_call`, `eth_estimateGas`,
            `eth_createAccessList`, `eth_simulateV1`, `eth_fillTransaction`) allowed to execute at once; further
            requests wait up to `MaxQueueWaitMs` for a slot and are answered with `LimitExceeded` (HTTP 503) beyond
            that. Defaults to `EthModuleConcurrentInstances`, so EVM traffic can never exceed the override-environment
            pool and never hits its instant rejection. Throughput plateaus at roughly one execution per logical
            processor: raising this past that converts the excess into queueing delay, not throughput.
            """)]
    int? EvmExecutionConcurrency { get; set; }

    [ConfigItem(
        Description = """
            The number of tracing JSON-RPC requests (`debug_trace*`, `trace_*`) allowed to execute at once;
            further requests wait up to `TracingMaxQueueWaitMs` for a slot and are answered with `LimitExceeded`
            (HTTP 503) beyond that. Defaults to the number of logical processors minus two, clamped to between two
            and sixteen: every slot needs a module instance, and each instance is a full block-processing pipeline
            kept for the lifetime of the process. Keep `DebugModuleConcurrentInstances` at or above this value,
            otherwise admitted `debug_trace*` requests wait for a module instance while holding their slot.
            """)]
    int? TracingConcurrency { get; set; }

    [ConfigItem(
        Description = """
            The number of proof-generating JSON-RPC requests (`proof_*`, `eth_getProof`) allowed to execute at
            once; further requests wait up to `ProofMaxQueueWaitMs` for a slot and are answered with `LimitExceeded`
            (HTTP 503) beyond that. Defaults to half the number of logical processors, clamped to between two and
            sixteen, for the same reason as `TracingConcurrency`.
            """)]
    int? ProofConcurrency { get; set; }

    [ConfigItem(
        Description = """
            The max time, in milliseconds, an EVM-executing JSON-RPC request (see `EvmExecutionConcurrency`) may
            wait for an execution slot. Requests whose predicted wait already exceeds it are rejected immediately
            with `LimitExceeded` (HTTP 503) rather than queued, and `0` disables queueing altogether: a request that
            finds no free slot is rejected at once. The predicted wait is
            `queued work no heavier than the request x mean service time per unit / slots`, with requests weighted
            by their `params` size (one unit per 128 KiB, at most 8) and lighter requests served first: at ~30 CPU-ms
            per request and 16 slots the default absorbs a burst of roughly 250 requests. A queue a few service
            times deep already keeps every slot busy under sustained overload; a longer one only adds latency to
            the requests it does serve. Tracing and proof requests have their own budgets
            (`TracingMaxQueueWaitMs`, `ProofMaxQueueWaitMs`).
            """,
        DefaultValue = "500")]
    int MaxQueueWaitMs { get; set; }

    [ConfigItem(
        Description = """
            The max time, in milliseconds, a tracing JSON-RPC request (see `TracingConcurrency`) may wait for an
            execution slot; the predicted-wait rejection and the `0` semantics of `MaxQueueWaitMs` apply against
            this budget. Defaults to `Timeout`, which is how long these requests waited for a module instance before
            the admission gate existed: tracing service times run into seconds, so a budget sized for `eth_call`
            would shed nearly every tracing request the moment its slots are full.
            """,
        DefaultValue = "null")]
    int? TracingMaxQueueWaitMs { get; set; }

    [ConfigItem(
        Description = """
            The max time, in milliseconds, a proof-generating JSON-RPC request (see `ProofConcurrency`) may wait
            for an execution slot; the predicted-wait rejection and the `0` semantics of `MaxQueueWaitMs` apply
            against this budget. Defaults to `Timeout`, for the same reason as `TracingMaxQueueWaitMs`.
            """,
        DefaultValue = "null")]
    int? ProofMaxQueueWaitMs { get; set; }

    [ConfigItem(
        Description = "The number of concurrent instances of the Trace RPC module (`trace_*`). Each instance is a full block-processing pipeline, created on first use and kept for the lifetime of the process. Defaults to `TracingConcurrency`, every slot of which needs an instance.")]
    int? TraceModuleConcurrentInstances { get; set; }

    [ConfigItem(
        Description = "The number of concurrent instances of the Proof RPC module (`proof_*`). Each instance is a full block-processing pipeline, created on first use and kept for the lifetime of the process. Defaults to `ProofConcurrency`, every slot of which needs an instance.")]
    int? ProofModuleConcurrentInstances { get; set; }

    [ConfigItem(Description = "The path to the JWT secret file required for the Engine API authentication.", DefaultValue = "null")]
    public string JwtSecretFile { get; set; }

    [ConfigItem(Description = "Whether to disable authentication of the Engine API. Should not be used in production environments.", DefaultValue = "false", HiddenFromDocs = true)]
    public bool UnsecureDevNoRpcAuthentication { get; set; }

    [ConfigItem(
        Description = "The max number of characters of a JSON-RPC request parameter printing to the log.",
        DefaultValue = "null")]
    int? MaxLoggedRequestParametersCharacters { get; set; }

    [ConfigItem(
        Description = "An array of the method names not to log.",
        DefaultValue = "[engine_newPayloadV1,engine_newPayloadV2,engine_newPayloadV3,engine_forkchoiceUpdatedV1,engine_forkchoiceUpdatedV2,flashbots_validateBuilderSubmissionV3,eth_signTransaction]")]
    public string[]? MethodsLoggingFiltering { get; set; }

    [ConfigItem(Description = "The Engine API host.", DefaultValue = "127.0.0.1")]
    string EngineHost { get; set; }

    [ConfigItem(Description = "The Engine API port.", DefaultValue = "null", IsPortOption = true)]
    int? EnginePort { get; set; }

    [ConfigItem(
        Description = "An array of additional JSON-RPC URLs to listen at with protocol and JSON-RPC namespace list for Engine API.",
        DefaultValue = "[Net,Eth,Subscribe,Web3]")]
    string[] EngineEnabledModules { get; set; }

    [ConfigItem(Description = "The max number of JSON-RPC requests in a batch.", DefaultValue = "1024")]
    int MaxBatchSize { get; set; }

    [ConfigItem(Description = "The maximum depth of JSON response object tree.", DefaultValue = "4096")]
    int JsonSerializationMaxDepth { get; set; }

    [ConfigItem(Description = "The max batch size limit for batched JSON-RPC calls.", DefaultValue = "33554432")]
    long? MaxBatchResponseBodySize { get; set; }

    [ConfigItem(Description = "The max block count limit for the `eth_simulate` JSON-RPC method.", DefaultValue = "256")]
    long? MaxSimulateBlocksCap { get; set; }

    [ConfigItem(Description = "The error margin used in the `eth_estimateGas` JSON-RPC method, in basis points.", DefaultValue = "150")]
    int EstimateErrorMargin { get; set; }

    [ConfigItem(Description = "Maximum total tx fee (gasPrice * gasLimit, in wei) the node will sign in eth_signTransaction. 0 disables the cap. Default 1 ETH.", DefaultValue = "1000000000000000000")]
    ulong RpcTxFeeCap { get; set; }

    [ConfigItem(Description = "Whether to enable eth_signTransaction. Disabled by default; enable only on nodes that explicitly manage unlocked accounts.", DefaultValue = "false")]
    bool EnableEthSignTransaction { get; set; }

    [ConfigItem(Description = "The JSON-RPC server CORS origins.", DefaultValue = "*")]
    string[] CorsOrigins { get; set; }

    [ConfigItem(Description = "Concurrency level of websocket connection.", DefaultValue = "1")]
    int WebSocketsProcessingConcurrency { get; set; }

    [ConfigItem(Description = "Concurrency level of IPC connection.", DefaultValue = "1")]
    int IpcProcessingConcurrency { get; set; }

    [ConfigItem(Description = "Enable per-method call metric", DefaultValue = "true")]
    bool EnablePerMethodMetrics { get; set; }

    [ConfigItem(Description = "The eth_filters timeout, in milliseconds.", DefaultValue = "900000")]
    int FiltersTimeout { get; set; }

    [ConfigItem(Description = "Preload rpc modules. Useful in rpc provider to reduce latency on first request.", DefaultValue = "false")]
    bool PreloadRpcModules { get; set; }

    [ConfigItem(
        Description = "Enable strict parsing rules for Block Params and Hashes in RPC requests. this will decrease compatibility but increase compliance with the spec.",
        DefaultValue = "true")]
    bool StrictHexFormat { get; set; }

    [ConfigItem(Description = "Default server-side wait, in milliseconds, for eth_sendRawTransactionSync when the caller omits the timeout argument.", DefaultValue = "20000")]
    int RpcTxSyncDefaultTimeoutMs { get; set; }

    [ConfigItem(Description = "Maximum server-side wait, in milliseconds, that eth_sendRawTransactionSync will accept; client-supplied timeouts above this are clamped down.", DefaultValue = "60000")]
    int RpcTxSyncMaxTimeoutMs { get; set; }

    [ConfigItem(
        Description = """
            Additional CIDR networks treated as trusted local sources for the JSON-RPC fast lane.
            Loopback and RFC1918 ranges (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16) are always trusted.
            Invalid entries are logged and ignored.
            """,
        DefaultValue = "[]")]
    string[] AdditionalTrustedNetworks { get; set; }
}
