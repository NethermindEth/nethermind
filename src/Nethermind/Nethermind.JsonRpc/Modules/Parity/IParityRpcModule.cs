// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Parity
{
    [RpcModule(ModuleType.Parity)]
    public interface IParityRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Returns a list of transactions currently in the queue. If address is provided, returns transactions only with given sender address.",
            IsImplemented = true,
            ExampleResponse = "{\"hash\":\"0x9372fe18622fd45569ef117644d4cda4af51d11bb3c72fa27690e78c9b0d7808\",\"nonce\":\"0x11b55\",\"blockHash\":null,\"blockNumber\":null,\"transactionIndex\":null,\"from\":\"0x89a3fc1d3c68f927be68d3de139980940a89fc80\",\"to\":\"0x89a3fc1d3c68f927be68d3de139980940a89fc80\",\"value\":\"0x0\",\"gasPrice\":\"0x3b9aca08\",\"gas\":\"0x7530\",\"input\":\"0x2f47e6a5c13bb151cad6f7297ceb6a197a9be6fdb3acbcfe1df3cad362525932\",\"raw\":\"0xf88683011b55843b9aca088275309489a3fc1d3c68f927be68d3de139980940a89fc8080a02f47e6a5c13bb151cad6f7297ceb6a197a9be6fdb3acbcfe1df3cad3625259322ba04cfe3030a781f8af08ebe69286a4fab707f00ce4e535c392ba8249527bdae5e5a002203d6802596ff141506437f7ae72b4391b2bdffafba45f8cb561cf5d24b456\",\"creates\":null,\"publicKey\":\"0xf409402c0b151206bb98e1031630681df4c046f0c278f920174daa14a34549fa2da52016ca659c0fe254c542fc3034c5a8da9f4d145fec6150db5ed19b4bc7ce\",\"chainId\":4,\"condition\":null,\"r\":\"0x4cfe3030a781f8af08ebe69286a4fab707f00ce4e535c392ba8249527bdae5e5\",\"s\":\"0x02203d6802596ff141506437f7ae72b4391b2bdffafba45f8cb561cf5d24b456\",\"v\":\"0x2b\",\"standardV\":\"0x0\"}, (...)")]
        ResultWrapper<ParityTransaction[]> parity_pendingTransactions([JsonRpcParameter(ExampleValue = "[\"0x78467cada5f1883e79fcf0f3ebfa50abeec8c820\"]")] Address? address = null);

        [JsonRpcMethod(Description = "Get receipts from all transactions from particular block, more efficient than fetching the receipts one-by-one.",
            IsImplemented = true,
            ExampleResponse = "{\"transactionHash\":\"0x5bea2e9354f63960beaf02942e7c791e61ae47ce6952115afcb3d7fbd5b8043b\",\"transactionIndex\":\"0x2\",\"blockHash\":\"0x31fda0834473452ad7df17e351bb540294fe9cf9752472468851f6b3a2c5f5aa\",\"blockNumber\":\"0x88de36\",\"cumulativeGasUsed\":\"0x50e46\",\"gasUsed\":\"0x5208\",\"from\":\"0xdd078bc60e500d379eaf30fc8658661ea0f2608a\",\"to\":\"0x5aab44fdc254f247dcb7ad89f248e7da346081d5\",\"contractAddress\":null,\"logs\":[],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000\",\"status\":\"0x1\",\"type\":\"0x0\"}, (...)")]
        ResultWrapper<ReceiptForRpc[]> parity_getBlockReceipts([JsonRpcParameter(ExampleValue = "latest")] BlockParameter blockParameter);

        [JsonRpcMethod(Description = "Returns the node enode URI.", IsImplemented = true, ExampleResponse = "enode://a9cfa3cb16b537e131b0f141b5ef0c0ab9bf0dbec7799c3fc7bf8a974ff3e74e9b3258951b285dfed07ab395049bcd65fed96116bb92561612682551ec458497@18.193.43.58:30303")]
        ResultWrapper<string> parity_enode();

        [JsonRpcMethod(Description = "", IsImplemented = true, ExampleResponse = "true")]
        ResultWrapper<bool> parity_setEngineSigner([JsonRpcParameter(ExampleValue = "[\"707Fc13C0eB628c074f7ff514Ae21ACaeE0ec072\",\"testPass\"]")] Address address, string password);

        [JsonRpcMethod(Description = "", IsImplemented = true)]
        ResultWrapper<bool> parity_setEngineSignerSecret(string privateKey);

        [JsonRpcMethod(Description = "", IsImplemented = true)]
        ResultWrapper<bool> parity_clearEngineSigner();

        [JsonRpcMethod(Description = "Returns connected peers. Peers with non-empty protocols have completed handshake.", IsImplemented = true)]
        ResultWrapper<ParityNetPeers> parity_netPeers();
    }
}
