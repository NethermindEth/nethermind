// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the JSON RPC service is enabled on node startup. Configure host and port if default values do not work for you.",
            DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Host for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC. If it does not work with 127.0.0.1 try something like 10.0.0.4 or 192.168.0.1",
            DefaultValue = "\"127.0.0.1\"")]
        string Host { get; set; }

        [ConfigItem(
            Description = "JSON RPC' timeout value given in milliseconds.",
            DefaultValue = "20000")]
        int Timeout { get; set; }

        [ConfigItem(
            Description = "The queued request limit for calls above the max concurrency amount for (" +
                          nameof(IEthRpcModule.eth_call) + ", " +
                          nameof(IEthRpcModule.eth_estimateGas) + ", " +
                          nameof(IEthRpcModule.eth_getLogs) + ", " +
                          nameof(IEthRpcModule.eth_newFilter) + ", " +
                          nameof(IEthRpcModule.eth_newBlockFilter) + ", " +
                          nameof(IEthRpcModule.eth_newPendingTransactionFilter) + ", " +
                          nameof(IEthRpcModule.eth_uninstallFilter) + "). " +
                          " If value is set to 0 limit won't be applied.",
            DefaultValue = "500")]
        int RequestQueueLimit { get; set; }

        [ConfigItem(
            Description = "Base file path for diagnostic JSON RPC recorder.",
            DefaultValue = "\"logs/rpc.{counter}.txt\"")]
        string RpcRecorderBaseFilePath { get; set; }

        [ConfigItem(
            Description = "Defines whether the JSON RPC diagnostic recording is enabled on node startup. Do not enable unless you are a DEV diagnosing issues with JSON RPC. Possible values: None/Request/Response/All.",
            DefaultValue = "None")]
        RpcRecorderState RpcRecorderState { get; set; }

        [ConfigItem(
            Description = "Port number for JSON RPC calls. Ensure the firewall is configured when enabling JSON RPC.",
            DefaultValue = "8545")]
        int Port { get; set; }

        [ConfigItem(
            Description = "Port number for JSON RPC web sockets calls. By default same port is used as regular JSON RPC. Ensure the firewall is configured when enabling JSON RPC.",
            DefaultValue = "8545")]
        int WebSocketsPort { get; set; }

        [ConfigItem(Description = "The path to connect a unix domain socket over.")]
        string IpcUnixDomainSocketPath { get; set; }

        [ConfigItem(
            Description = "Defines which RPC modules should be enabled. Built in modules are: Admin, Clique, Consensus, Db, Debug, Deposit, Erc20, Eth, Evm, Health Mev, NdmConsumer, NdmProvider, Net, Nft, Parity, Personal, Proof, Subscribe, Trace, TxPool, Vault, Web3.",
            DefaultValue = "[Eth, Subscribe, Trace, TxPool, Web3, Personal, Proof, Net, Parity, Health, Rpc]")]
        string[] EnabledModules { get; set; }

        [ConfigItem(
            Description = "Defines additional RPC urls to listen on. Example url format: http://localhost:8550|http;wss|engine;eth;net;subscribe",
            DefaultValue = "[]")]
        string[] AdditionalRpcUrls { get; set; }

        [ConfigItem(
            Description = "Gas limit for eth_call and eth_estimateGas",
            DefaultValue = "100000000")]
        long? GasCap { get; set; }

        [ConfigItem(
            Description = "Interval between the JSON RPC stats report log",
            DefaultValue = "300")]
        int ReportIntervalSeconds { get; set; }

        [ConfigItem(
            Description = "Buffer responses before sending them to client. This allows to set Content-Length in response instead of using Transfer-Encoding: chunked. This may degrade performance on big responses. Max buffered response size is 2GB, chunked responses can be bigger.",
            DefaultValue = "false")]
        bool BufferResponses { get; set; }

        [ConfigItem(
            Description = "A path to a file that contains a list of new-line separated approved JSON RPC calls",
            DefaultValue = "Data/jsonrpc.filter")]
        string CallsFilterFilePath { get; set; }

        [ConfigItem(
            Description = "Max HTTP request body size",
            DefaultValue = "30000000")]
        long? MaxRequestBodySize { get; set; }

        [ConfigItem(
            Description = "Number of concurrent instances for non-sharable calls (" +
                          nameof(IEthRpcModule.eth_call) + ", " +
                          nameof(IEthRpcModule.eth_estimateGas) + ", " +
                          nameof(IEthRpcModule.eth_getLogs) + ", " +
                          nameof(IEthRpcModule.eth_newFilter) + ", " +
                          nameof(IEthRpcModule.eth_newBlockFilter) + ", " +
                          nameof(IEthRpcModule.eth_newPendingTransactionFilter) + ", " +
                          nameof(IEthRpcModule.eth_uninstallFilter) + "). " +
                          "This will limit load on the node CPU and IO to reasonable levels. " +
                          "If this limit is exceeded on Http calls 503 Service Unavailable will be returned along with Json RPC error. " +
                          "Defaults to number of logical processes.")]
        int? EthModuleConcurrentInstances { get; set; }

        [ConfigItem(Description = "Path to file with hex encoded secret for jwt authentication")]
        public string JwtSecretFile { get; set; }

        [ConfigItem(Description = "It shouldn't be set to true for production nodes. If set to true all modules can work without RPC authentication.", DefaultValue = "false", HiddenFromDocs = true)]
        public bool UnsecureDevNoRpcAuthentication { get; set; }

        [ConfigItem(
            Description = "Limits the Maximum characters printing to log for parameters of any Json RPC service request",
            DefaultValue = "null")]
        int? MaxLoggedRequestParametersCharacters { get; set; }

        [ConfigItem(
            Description = "Defines method names of Json RPC service requests to NOT log. Example: {\"eth_blockNumber\"} will not log \"eth_blockNumber\" requests.",
            DefaultValue = "[engine_newPayloadV1, engine_newPayloadV2, engine_newPayloadV3, engine_forkchoiceUpdatedV1, engine_forkchoiceUpdatedV2]")]
        public string[]? MethodsLoggingFiltering { get; set; }

        [ConfigItem(
            Description = "Host for Execution Engine calls. Ensure the firewall is configured when enabling JSON RPC. If it does not work with 127.0.0.1 try something like 10.0.0.4 or 192.168.0.1",
            DefaultValue = "\"127.0.0.1\"")]
        string EngineHost { get; set; }

        [ConfigItem(
            Description = "Port for Execution Engine calls. Ensure the firewall is configured when enabling JSON RPC.",
            DefaultValue = "null")]
        int? EnginePort { get; set; }

        [ConfigItem(
            Description = "Defines which RPC modules should be enabled Execution Engine port. Built in modules are: Admin, Clique, Consensus, Db, Debug, Deposit, Erc20, Eth, Evm, Health Mev, NdmConsumer, NdmProvider, Net, Nft, Parity, Personal, Proof, Subscribe, Trace, TxPool, Vault, Web3.",
            DefaultValue = "[Net, Eth, Subscribe, Web3]")]
        string[] EngineEnabledModules { get; set; }

        [ConfigItem(
            Description = "Limit batch size for batched json rpc call",
            DefaultValue = "1024")]
        int MaxBatchSize { get; set; }

        [ConfigItem(
            Description = "Max response body size when using batch requests, subsequent requests are trimmed",
            DefaultValue = "30000000")]
        long? MaxBatchResponseBodySize { get; set; }
    }
}
