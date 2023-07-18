// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class JsonRpcConfig : IJsonRpcConfig
    {
        public static readonly JsonRpcConfig Default = new();
        private int? _webSocketsPort;
        public bool Enabled { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public int Timeout { get; set; } = 20000;
        public int MaxPendingSharedRequests { get; set; } = 500;
        public string RpcRecorderBaseFilePath { get; set; } = "logs/rpc.{counter}.txt";

        public RpcRecorderState RpcRecorderState { get; set; } = RpcRecorderState.None;

        public int Port { get; set; } = 8545;

        public int WebSocketsPort
        {
            get => _webSocketsPort ?? Port;
            set => _webSocketsPort = value;
        }

        public string? IpcUnixDomainSocketPath { get; set; } = null;

        public string[] EnabledModules { get; set; } = ModuleType.DefaultModules.ToArray();
        public string[] AdditionalRpcUrls { get; set; } = Array.Empty<string>();
        public long? GasCap { get; set; } = 100000000;
        public int ReportIntervalSeconds { get; set; } = 300;
        public bool BufferResponses { get; set; }
        public string CallsFilterFilePath { get; set; } = "Data/jsonrpc.filter";
        public long? MaxRequestBodySize { get; set; } = 30000000;
        public int? EthModuleConcurrentInstances { get; set; } = null;
        public string JwtSecretFile { get; set; } = "keystore/jwt-secret";
        public bool UnsecureDevNoRpcAuthentication { get; set; }
        public int? MaxLoggedRequestParametersCharacters { get; set; } = null;
        public string[]? MethodsLoggingFiltering { get; set; } =
        {
            "engine_newPayloadV1",
            "engine_newPayloadV2",
            "engine_newPayloadV3",
            "engine_forkchoiceUpdatedV1",
            "engine_forkchoiceUpdatedV2"
        };
        public string EngineHost { get; set; } = "127.0.0.1";
        public int? EnginePort { get; set; } = null;
        public string[] EngineEnabledModules { get; set; } = ModuleType.DefaultEngineModules.ToArray();
        public int MaxBatchSize { get; set; } = 1024;
        public long? MaxBatchResponseBodySize { get; set; } = 30.MB();
    };
};

