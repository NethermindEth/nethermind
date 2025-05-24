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
        Description = "Get the output root at a specific block",
        IsSharable = true,
        ExampleResponse = """
        {
            "version": "0x0000000000000000000000000000000000000000000000000000000000000000",
            "outputRoot": "0xd13f0fb77010a8d8d000694b290607e961a96679a5f95dd2729dbd8449d61f71",
            "blockRef": {
                "hash": "0x2b8e886f2ba10fbb12c56917bd7d645149e80a6b63ed3a22706ff77f4f68b31f",
                "number": 27982526,
                "parentHash": "0xb0ba7dd7e480dba77138bb8e7c3d8336ecf8506d43c72cd728c56e18f9d855de",
                "timestamp": 1747767592,
                "l1origin": {
                    "hash": "0xec9cffbe80bf9464bb22696cc42044e0dae1ddcf1dc731e0f71d336dae6cf42a",
                    "number": 8369709
                },
                "sequenceNumber": 3
            },
            "withdrawalStorageRoot": "0x086887b2594a26b67d79630404998a03636eb5be95b5422079fff0856cc436e8",
            "stateRoot": "0xeef23eb186808bb06e74b915cd0cc028fad7fbff016b9d5f3fb75cd2098b4ff9",
            "syncStatus": {
                "current_l1": {
                    "hash": "0x7436683b06cbe2a6e5ddee3b56662a06dad9d1ded8aaa1025db7d00fd40ad660",
                    "number": 8369912,
                    "parentHash": "0xda0c6532118c2fed831c452cf072d997ef93ffacb2229882b10375b51d09eed2",
                    "timestamp": 1747769952
                },
                "current_l1_finalized": {
                    "hash": "0x702499355918ad1c1568f9e3552b6f8cdf7c55020f856c127f4d99f651a71f5c",
                    "number": 8369816,
                    "parentHash": "0xfd46bb323d403a18cd7e7f7dd75b25a6fc11042a90d20bedf5cc7ca21be7e903",
                    "timestamp": 1747768800
                },
                "head_l1": {
                    "hash": "0xda0c6532118c2fed831c452cf072d997ef93ffacb2229882b10375b51d09eed2",
                    "number": 8369911,
                    "parentHash": "0x0e99acec22398878f90f4061894669ac3d6e92409b849c849d84ead826c6f4b2",
                    "timestamp": 1747769940
                },
                "safe_l1": {
                    "hash": "0x795d9561c7c5e9cbc26f3834d6911003a4ba1c57b0f7cb2d07f2bba1299481a6",
                    "number": 8369848,
                    "parentHash": "0x35968c6c0e0b46f2096a4dfa19f8176640d774a25d76e5a899e3b1f495d3ed89",
                    "timestamp": 1747769184
                },
                "finalized_l1": {
                    "hash": "0x702499355918ad1c1568f9e3552b6f8cdf7c55020f856c127f4d99f651a71f5c",
                    "number": 8369816,
                    "parentHash": "0xfd46bb323d403a18cd7e7f7dd75b25a6fc11042a90d20bedf5cc7ca21be7e903",
                    "timestamp": 1747768800
                },
                "unsafe_l2": {
                    "hash": "0x2ff9b8127f79a19a5fbf1656612eade0763c5ba6be6f5f5b954d5ca7d3b24dbe",
                    "number": 27983710,
                    "parentHash": "0x51bffa9fedb78364086ba45d38b82c24d3397a0dcc51cbc68cecc7ebc9b1f4f8",
                    "timestamp": 1747769960,
                    "l1origin": {
                        "hash": "0x55421b87f34d649251e1b4bb8e3cacd8de6b994a84a0e386cbefb69dd251e830",
                        "number": 8369907
                    },
                    "sequenceNumber": 0
                },
                "safe_l2": {
                    "hash": "0x12c18807eb1578776d9237b33fde9baeec8b9b6f9594eae8bc5208b177652774",
                    "number": 27983607,
                    "parentHash": "0xe514721b50d0abb179b5fdb099ed0f8d8a5e2c75d42ba5864337f03c73e07f31",
                    "timestamp": 1747769754,
                    "l1origin": {
                        "hash": "0xb27ea6ae8fea77a431041ee6edb6858d8243cffaf21a1657b10e8c5e080847f2",
                        "number": 8369889
                    },
                    "sequenceNumber": 6
                },
                "finalized_l2": {
                    "hash": "0xaf6ec2ad20956631f6da172fa4dd3f7ff2aa4a8624e3b85bc452a7112430f193",
                    "number": 27983037,
                    "parentHash": "0xf822e3d49edc88c7eb92c82d4aaf0ac6996e4d0ff2bf301b70d9e0db200ad218",
                    "timestamp": 1747768614,
                    "l1origin": {
                        "hash": "0x01d7decde6bd1557e8192cd42980c8ecfc9f17651a8be8106a0bfaeac5400b67",
                        "number": 8369794
                    },
                    "sequenceNumber": 2
                },
                "pending_safe_l2": {
                    "hash": "0x12c18807eb1578776d9237b33fde9baeec8b9b6f9594eae8bc5208b177652774",
                    "number": 27983607,
                    "parentHash": "0xe514721b50d0abb179b5fdb099ed0f8d8a5e2c75d42ba5864337f03c73e07f31",
                    "timestamp": 1747769754,
                    "l1origin": {
                        "hash": "0xb27ea6ae8fea77a431041ee6edb6858d8243cffaf21a1657b10e8c5e080847f2",
                        "number": 8369889
                    },
                    "sequenceNumber": 6
                },
                "cross_unsafe_l2": {
                    "hash": "0x2ff9b8127f79a19a5fbf1656612eade0763c5ba6be6f5f5b954d5ca7d3b24dbe",
                    "number": 27983710,
                    "parentHash": "0x51bffa9fedb78364086ba45d38b82c24d3397a0dcc51cbc68cecc7ebc9b1f4f8",
                    "timestamp": 1747769960,
                    "l1origin": {
                        "hash": "0x55421b87f34d649251e1b4bb8e3cacd8de6b994a84a0e386cbefb69dd251e830",
                        "number": 8369907
                    },
                    "sequenceNumber": 0
                },
                "local_safe_l2": {
                    "hash": "0x12c18807eb1578776d9237b33fde9baeec8b9b6f9594eae8bc5208b177652774",
                    "number": 27983607,
                    "parentHash": "0xe514721b50d0abb179b5fdb099ed0f8d8a5e2c75d42ba5864337f03c73e07f31",
                    "timestamp": 1747769754,
                    "l1origin": {
                        "hash": "0xb27ea6ae8fea77a431041ee6edb6858d8243cffaf21a1657b10e8c5e080847f2",
                        "number": 8369889
                    },
                    "sequenceNumber": 6
                }
            }
        }
        """)]
    public Task<ResultWrapper<OptimismOutputAtBlock>> optimism_outputAtBlock(ulong blockNumber);

    [JsonRpcMethod(
        IsImplemented = false,
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
        IsImplemented = false,
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
        IsImplemented = false,
        Description = "Get the software version.",
        IsSharable = true,
        ExampleResponse = "1.31.10")]
    public Task<ResultWrapper<string>> optimism_version();
}
