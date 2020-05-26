using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Baseline
{
    public class MockGetLogs
    {
    //    {"jsonrpc":"2.0","result":
    //    [{"address":"0x83c82edd1605ac37d9065d784fdc000b20e9879d",
    //    "blockHash":"0xbbf3682375dae572acfb63c67f862dcdf59e96e043d44152cca7ebefa8c14cec",
    //    "blockNumber":"0x5",
    //    "data":"0x0000000000000000000000000000000000000000000000000000000000000000f23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b61ace302d4fce7493773820dd2a7ecb84a16c199ff2607af77adff00000000000",
    //    "logIndex":"0x0",
    //    "removed":false,
    //    "topics":["0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0"],
    //    "transactionHash":"0xbe45ba4ec5fdfa14239c5e345f7e99dc7f7a6d6cd05e7e52b1fc5254bc712b9b",
    //    "transactionIndex":"0x0",
    //    "transactionLogIndex":"0x0"}],"id":74}
        public string jsonrpc = "2.0";
        public string address = "0x83c82edd1605ac37d9065d784fdc000b20e9879d";
        public string blockHash = "0xbbf3682375dae572acfb63c67f862dcdf59e96e043d44152cca7ebefa8c14cec";
        public string blockNumber = "0x5";
        public string data = "0x0000000000000000000000000000000000000000000000000000000000000000f23682e2f2e9ea141d4663defc40f72a76c35b35d8cad6e0161901f2a967c9b61ace302d4fce7493773820dd2a7ecb84a16c199ff2607af77adff00000000000";
        public string logIndex = "0x0";
        public bool removed = false;
        public string[] topcics = new string[] {"0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0"};
        public string transactionHash = "0xbe45ba4ec5fdfa14239c5e345f7e99dc7f7a6d6cd05e7e52b1fc5254bc712b9b";
        public string transactionIndex = "0x0";
        public string transactionLogIndex = "0x0";
        public int id = 74;
    }
}