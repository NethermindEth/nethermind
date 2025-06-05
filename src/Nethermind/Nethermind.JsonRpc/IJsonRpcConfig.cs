// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Serialization.Json;

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
            The max number of concurrent requests in the queue for:

            - `eth_call`
            - `eth_estimateGas`
            - `eth_getLogs`
            - `eth_newFilter`
            - `eth_newBlockFilter`
            - `eth_newPendingTransactionFilter`
            - `eth_uninstallFilter`

            `0` to lift the limit.
            """,
        DefaultValue = "500")]
    int RequestQueueLimit { get; set; }

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
        DefaultValue = "[Eth,Subscribe,Trace,TxPool,Web3,Personal,Proof,Net,Parity,Health,Rpc]")]
    string[] EnabledModules { get; set; }

    [ConfigItem(
        Description = "An array of additional JSON-RPC URLs to listen at with protocol and JSON-RPC namespace list. For instance, `[http://localhost:8546|http;ws|eth;web3]`.",
        DefaultValue = "[]")]
    string[] AdditionalRpcUrls { get; set; }

    [ConfigItem(Description = "The maximum gas limit for `eth_call` and `eth_estimateGas`.", DefaultValue = "100000000")]
    long? GasCap { get; set; }

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
        Description = """
            The number of concurrent instances for non-sharable calls:

            - `eth_call`
            - `eth_estimateGas`
            - `eth_getLogs`
            - `eth_newBlockFilter`
            - `eth_newFilter`
            - `eth_newPendingTransactionFilter`
            - `eth_uninstallFilter`

            This limits the load on the CPU and I/O to reasonable levels. If the limit is exceeded, HTTP 503 is returned along with the JSON-RPC error. Defaults to the number of logical processors.
            """)]
    int? EthModuleConcurrentInstances { get; set; }

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
        DefaultValue = "[engine_newPayloadV1,engine_newPayloadV2,engine_newPayloadV3,engine_forkchoiceUpdatedV1,engine_forkchoiceUpdatedV2,flashbots_validateBuilderSubmissionV3]")]
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

    [ConfigItem(Description = "The maximum depth of JSON response object tree.", DefaultValue = "128")]
    int JsonSerializationMaxDepth { get; set; }

    [ConfigItem(Description = "The max batch size limit for batched JSON-RPC calls.", DefaultValue = "33554432")]
    long? MaxBatchResponseBodySize { get; set; }

    [ConfigItem(Description = "The max block count limit for the `eth_simulate` JSON-RPC method.", DefaultValue = "256")]
    long? MaxSimulateBlocksCap { get; set; }

    [ConfigItem(Description = "The error margin used in the `eth_estimateGas` JSON-RPC method, in basis points.", DefaultValue = "150")]
    int EstimateErrorMargin { get; set; }

    [ConfigItem(Description = "The JSON-RPC server CORS origins.", DefaultValue = "*")]
    string[] CorsOrigins { get; set; }

    [ConfigItem(Description = "Concurrency level of websocket connection.", DefaultValue = "1")]
    int WebSocketsProcessingConcurrency { get; set; }

    [ConfigItem(Description = "Concurrency level of IPC connection.", DefaultValue = "1")]
    int IpcProcessingConcurrency { get; set; }

    [ConfigItem(Description = "Enable per-method call metric", DefaultValue = "false")]
    bool EnablePerMethodMetrics { get; set; }

    [ConfigItem(Description = "The eth_filters timeout, in milliseconds.", DefaultValue = "900000")]
    int FiltersTimeout { get; set; }
}
