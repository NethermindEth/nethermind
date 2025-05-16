// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

public class JsonRpcConfig : IJsonRpcConfig
{
    public static readonly JsonRpcConfig Default = new();
    private int? _webSocketsPort;
    private string[] _enabledModules = ModuleType.DefaultModules.ToArray();
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Timeout { get; set; } = 20000;
    public int RequestQueueLimit { get; set; } = 500;
    public string RpcRecorderBaseFilePath { get; set; } = "logs/rpc.{counter}.txt";

    public RpcRecorderState RpcRecorderState { get; set; } = RpcRecorderState.None;

    public int Port { get; set; } = 8545;

    public int WebSocketsPort
    {
        get => _webSocketsPort ?? Port;
        set => _webSocketsPort = value;
    }

    public string? IpcUnixDomainSocketPath { get; set; } = null;

    public string[] EnabledModules
    {
        get => _enabledModules;
        set
        {
            _enabledModules = value.Where(static m => !string.IsNullOrWhiteSpace(m)).ToArray();
        }
    }

    public string[] AdditionalRpcUrls { get; set; } = [];
    public long? GasCap { get; set; } = 100000000;
    public int ReportIntervalSeconds { get; set; } = 300;
    public bool BufferResponses { get; set; }
    public string CallsFilterFilePath { get; set; } = "Data/jsonrpc.filter";
    public long? MaxRequestBodySize { get; set; } = 30000000;
    public int MaxLogsPerResponse { get; set; } = 20_000;
    public int? EthModuleConcurrentInstances { get; set; } = null;
    public string JwtSecretFile { get; set; } = null;
    public bool UnsecureDevNoRpcAuthentication { get; set; }
    public int? MaxLoggedRequestParametersCharacters { get; set; } = null;
    public string[]? MethodsLoggingFiltering { get; set; } =
    {
        "engine_newPayloadV1",
        "engine_newPayloadV2",
        "engine_newPayloadV3",
        "engine_forkchoiceUpdatedV1",
        "engine_forkchoiceUpdatedV2",
        "flashbots_validateBuilderSubmissionV3"
    };
    public string EngineHost { get; set; } = "127.0.0.1";
    public int? EnginePort { get; set; } = null;
    public string[] EngineEnabledModules { get; set; } = ModuleType.DefaultEngineModules.ToArray();
    public int MaxBatchSize { get; set; } = 1024;
    public int JsonSerializationMaxDepth { get; set; } = EthereumJsonSerializer.DefaultMaxDepth;
    public long? MaxBatchResponseBodySize { get; set; } = 32.MiB();
    public long? MaxSimulateBlocksCap { get; set; } = 256;
    public int EstimateErrorMargin { get; set; } = 150;
    public string[] CorsOrigins { get; set; } = ["*"];
    public int WebSocketsProcessingConcurrency { get; set; } = 1;
    public int IpcProcessingConcurrency { get; set; } = 1;
    public bool EnablePerMethodMetrics { get; set; } = false;
    public int FiltersTimeout { get; set; } = 900000;
};
