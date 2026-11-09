// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.JsonRpc.Modules.TxPool;

[RpcModule(ModuleType.TxPool)]
public interface ITxPoolRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Returns a tx pool status.", IsImplemented = true, ExampleResponse = "{\"pending\":\"0x3f2\",\"queued\":\"0xe\"}")]
    ResultWrapper<TxPoolStatus> txpool_status();

    [JsonRpcMethod(Description = "Returns tx pool content.",
        IsImplemented = true,
        ExampleResponse = "{\"0x0f990ef7ec160f01af7148b74cc8a86fe46c551e\":{\"153\":{\"hash\":\"0x84f6f2e5d24b9a0c25bd7018adbbf4388b2c07842782f73d5ddc389906d5f2c8\",\"nonce\":\"0x99\",\"blockHash\":null,\"blockNumber\":null,\"blockTimestamp\":null,\"transactionIndex\":null,\"from\":\"0x0f990ef7ec160f01af7148b74cc8a86fe46c551e\",\"to\":\"0x1b4e4664de1d57b665b4bf3523cbccf007766de3\",\"value\":\"0xc8\",\"gasPrice\":\"0x3b9aca08\",\"gas\":\"0x1c9c37f\",\"data\":\"0xaeeb89600000000000000000000000000000000000000000000000000000000000000001\",\"input\":\"0xaeeb89600000000000000000000000000000000000000000000000000000000000000001\",\"type\":\"0x0\",\"v\":\"0x2c\",\"s\":\"0x20158ce3f4f9c65f8c657c0d91bbfb43632b2951f6192bca8fb3a25c26dd81d5\",\"r\":\"0x2814d998f2a78dd4f37461485d88158a32ef5dcfa8c57e224b3ea77536df01b1\"}}}")]
    ResultWrapper<TxPoolContent> txpool_content();

    [JsonRpcMethod(Description = "Returns tx pool content for a specific sender address.",
        IsImplemented = true,
        ExampleResponse = "{\"pending\":{\"806\":{\"blockHash\":null,\"blockNumber\":null,\"blockTimestamp\":null,\"from\":\"0x0216d5032f356960cd3749c31ab34eeff21b3395\",\"gas\":\"0x5208\",\"gasPrice\":\"0xba43b7400\",\"hash\":\"0xaf953a2d01f55cfe080c0c94150a60105e8ac3d51153058a1f03dd239dd08586\",\"nonce\":\"0x326\",\"to\":\"0x7f69a91a3cf4be60020fb58b893b7cbb65376db8\",\"transactionIndex\":null,\"value\":\"0x19a99f0cf456000\"}},\"queued\":{}}")]
    ResultWrapper<TxPoolContentFrom> txpool_contentFrom(Address address);

    [JsonRpcMethod(Description = "Returns a detailed info on tx pool transactions.",
        IsImplemented = true,
        ExampleResponse = "{\"pending\":{\"0xb7705aE4c6F81B66cdB323C65f4E8133690fC099\":{\"0\":\"0x65942Aaf2C32a1aca4f14e82e94fCE91960893a2: 1000000000000000000 wei + 21000 gas × 30000000000 wei\",\"1\":\"contract creation: 0 wei + 1000000 gas × 30000000000 wei\"}},\"queued\":{\"0x942921b14f1B1C385CD7e0cc2eF7aBe5598c8358\":{\"7\":\"0x76e68A8696537e4141926F3e528733af9e237d69: 0 wei + 50000 gas × 2000000000 wei\"}}}")]
    ResultWrapper<TxPoolInspection> txpool_inspect();
}
