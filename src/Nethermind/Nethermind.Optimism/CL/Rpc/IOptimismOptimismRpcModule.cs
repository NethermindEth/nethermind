// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Optimism.Cl.Rpc;

[RpcModule("Optimism")]
public interface IOptimismOptimismRpcModule : IRpcModule
{
    [JsonRpcMethod(
        IsImplemented = false,
        Description = "TODO",
        IsSharable = true,
        ExampleResponse = "TODO")]
    public Task<ResultWrapper<int>> optimism_outputAtBlock();

    [JsonRpcMethod(
        IsImplemented = true,
        Description = "Get the synchronization status.",
        IsSharable = true,
        ExampleResponse = """
        {
            "current_l1": {
                "hash": "0xff3b3253058411b727ac662f4c9ae1698918179e02ecebd304beb1a1ae8fc4fd",
                "number": 4427350,
                "parentHash": "0xb26586390c3f04678706dde13abfb5c6e6bb545e59c22774e651db224b16cd48",
                "timestamp": 1696478784
            },
            "current_l1_finalized": {
                "hash": "0x7157f91b8ae21ef869c604e5b268e392de5aa69a9f44466b9b0f838d56426541",
                "number": 4706784,
                "parentHash": "0x1ac2612a500b9facd650950b8755d97cf2470818da2d88552dea7cd563e86a17",
                "timestamp": 1700160084
            },
            "head_l1": {
                "hash": "0x6110a8e6ed4c4aaab20477a3eac81bf99e505bf6370cd4d2e3c6d34aa5f4059a",
                "number": 4706863,
                "parentHash": "0xee8a9cba5d93481f11145c24890fd8f536384f3c3c043f40006650538fbdcb56",
                "timestamp": 1700161272
            },
            "safe_l1": {
                "hash": "0x8407c9968ce278ab435eeaced18ba8f2f94670ad9d3bdd170560932cf46e2804",
                "number": 4706811,
                "parentHash": "0x6593cccab3e772776418ff691f6e4e75597af18505373522480fdd97219c06ef",
                "timestamp": 1700160480
            },
            "finalized_l1": {
                "hash": "0x7157f91b8ae21ef869c604e5b268e392de5aa69a9f44466b9b0f838d56426541",
                "number": 4706784,
                "parentHash": "0x1ac2612a500b9facd650950b8755d97cf2470818da2d88552dea7cd563e86a17",
                "timestamp": 1700160084
            },
            "unsafe_l2": {
                "hash": "0x9a3b2edab72150de252d45cabe2f1ac57d48ddd52bb891831ffed00e89408fe4",
                "number": 2338094,
                "parentHash": "0x935b94ec0bac0e63c67a870b1a97d79e3fa84dda86d31996516cb2f940753f53",
                "timestamp": 1696478728,
                "l1origin": {
                    "hash": "0x38731e0a6eeb40091f0c4a00650e911c57d054aaeb5b158f55cd5705fa6a3ebf",
                    "number": 4427339
                },
                "sequenceNumber": 3
            },
            "safe_l2": {
                "hash": "0x9a3b2edab72150de252d45cabe2f1ac57d48ddd52bb891831ffed00e89408fe4",
                "number": 2338094,
                "parentHash": "0x935b94ec0bac0e63c67a870b1a97d79e3fa84dda86d31996516cb2f940753f53",
                "timestamp": 1696478728,
                "l1origin": {
                    "hash": "0x38731e0a6eeb40091f0c4a00650e911c57d054aaeb5b158f55cd5705fa6a3ebf",
                    "number": 4427339
                },
                "sequenceNumber": 3
            },
            "finalized_l2": {
                "hash": "0x285b03afb46faad747be1ca7ab6ef50ef0ff1fe04e4eeabafc54f129d180fad2",
                "number": 2337942,
                "parentHash": "0x7e7f36cba1fd1ccdcdaa81577a1732776a01c0108ab5f98986cf997724eb48ac",
                "timestamp": 1696478424,
                "l1origin": {
                    "hash": "0x983309dadf7e0ab8447f3050f2a85b179e9acde1cd884f883fb331908c356412",
                    "number": 4427314
                },
                "sequenceNumber": 7
            },
            "pending_safe_l2": {
                "hash": "0x9a3b2edab72150de252d45cabe2f1ac57d48ddd52bb891831ffed00e89408fe4",
                "number": 2338094,
                "parentHash": "0x935b94ec0bac0e63c67a870b1a97d79e3fa84dda86d31996516cb2f940753f53",
                "timestamp": 1696478728,
                "l1origin": {
                    "hash": "0x38731e0a6eeb40091f0c4a00650e911c57d054aaeb5b158f55cd5705fa6a3ebf",
                    "number": 4427339
                },
                "sequenceNumber": 3
            },
            "queued_unsafe_l2": {
                "hash": "0x3af253f5b993f58fffdd5e594b3f53f5b7b254cdc18f4bdb13ea7331149942db",
                "number": 4054795,
                "parentHash": "0x284b7dc92bac97be8ec3b2cf548e75208eb288704de381f2557938ecdf86539d",
                "timestamp": 1699912130,
                "l1origin": {
                    "hash": "0x1490a63c372090a0331e05e63ec6a7a6e84835f91776306531f28b4217394d76",
                    "number": 4688196
                },
                "sequenceNumber": 2
            },
            "engine_sync_target": {
                "hash": "0x9a3b2edab72150de252d45cabe2f1ac57d48ddd52bb891831ffed00e89408fe4",
                "number": 2338094,
                "parentHash": "0x935b94ec0bac0e63c67a870b1a97d79e3fa84dda86d31996516cb2f940753f53",
                "timestamp": 1696478728,
                "l1origin": {
                    "hash": "0x38731e0a6eeb40091f0c4a00650e911c57d054aaeb5b158f55cd5705fa6a3ebf",
                    "number": 4427339
                },
                "sequenceNumber": 3
            }
        }
        """)]
    public Task<ResultWrapper<OptimismSyncStatus>> optimism_syncStatus();

    [JsonRpcMethod(
        IsImplemented = true,
        Description = "Get the rollup configuration parameters.",
        IsSharable = true,
        ExampleResponse = """
        {
            "genesis": {
                "l1": {
                    "hash": "0x48f520cf4ddaf34c8336e6e490632ea3cf1e5e93b0b2bc6e917557e31845371b",
                    "number": 4071408
                },
                "l2": {
                    "hash": "0x102de6ffb001480cc9b8b548fd05c34cd4f46ae4aa91759393db90ea0409887d",
                    "number": 0
                },
                "l2_time": 1691802540,
                "system_config": {
                    "batcherAddr": "0x8f23bb38f531600e5d8fddaaec41f13fab46e98c",
                    "overhead": "0x00000000000000000000000000000000000000000000000000000000000000bc",
                    "scalar": "0x00000000000000000000000000000000000000000000000000000000000a6fe0",
                    "gasLimit": 30000000
                }
            },
            "block_time": 2,
            "max_sequencer_drift": 600,
            "seq_window_size": 3600,
            "channel_timeout": 300,
            "l1_chain_id": 11155111,
            "l2_chain_id": 11155420,
            "regolith_time": 0,
            "canyon_time": 1699981200,
            "batch_inbox_address": "0xff00000000000000000000000000000011155420",
            "deposit_contract_address": "0x16fc5058f25648194471939df75cf27a2fdc48bc",
            "l1_system_config_address": "0x034edd2a225f7f429a63e0f1d2084b9e0a93b538",
            "protocol_versions_address": "0x79add5713b383daa0a138d3c4780c7a1804a8090"
        }
        """)]
    public Task<ResultWrapper<OptimismRollupConfig>> optimism_rollupConfig();

    [JsonRpcMethod(
        IsImplemented = true,
        Description = "Get the software version.",
        IsSharable = true,
        ExampleResponse = "1.31.10")]
    public Task<ResultWrapper<string>> optimism_version();
}
