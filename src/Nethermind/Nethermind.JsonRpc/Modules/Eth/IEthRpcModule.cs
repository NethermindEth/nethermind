//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.State.Proofs;

namespace Nethermind.JsonRpc.Modules.Eth
{
    [RpcModule(ModuleType.Eth)]
    public interface IEthRpcModule : IRpcModule
    {
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns ChainID", 
            IsSharable = true, 
            ExampleResponse = "0x5")]
        ResultWrapper<ulong> eth_chainId();
        
        [JsonRpcMethod(IsImplemented = true,
            Description = "Returns ETH protocol version", IsSharable = true, ExampleResponse = "0x41")]
        ResultWrapper<string> eth_protocolVersion();
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns syncing status", 
            IsSharable = true, 
            ExampleResponse = "{\"startingBlock\":\"0x0\",\"currentBlock\":\"0x0\",\"highestBlock\":\"0x4df8a4\"},\"id\":1}")]
        ResultWrapper<SyncingResult> eth_syncing();
        
        [JsonRpcMethod(IsImplemented = false, 
            Description = "Returns miner's coinbase", 
            IsSharable = true, 
            ExampleResponse = "0x0000000000000000000000000000000000000000")]
        ResultWrapper<Address> eth_coinbase();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns mining status", IsSharable = true)]
        ResultWrapper<bool?> eth_mining();
        
        [JsonRpcMethod(IsImplemented = false, Description = "Returns full state snapshot", IsSharable = true)]
        ResultWrapper<byte[]> eth_snapshot();
        
        [JsonRpcMethod(IsImplemented = false, 
            Description = "Returns mining hashrate", 
            IsSharable = true, 
            ExampleResponse = "0x0")]
        ResultWrapper<UInt256?> eth_hashrate();
        
        [JsonRpcMethod(IsImplemented = false, 
            Description = "Returns miner's gas price", 
            IsSharable = true, 
            ExampleResponse = "0x4a817c800" )]
        ResultWrapper<UInt256?> eth_gasPrice();
        
        [JsonRpcMethod(IsImplemented = false, 
            Description = "Returns accounts", 
            IsSharable = true, 
            ExampleResponse = "[\"0x9b96a7841d6e0b76872c85c86082959189a27342\"]")]
        ResultWrapper<IEnumerable<Address>> eth_accounts();
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns current block number", 
            IsSharable = true, 
            ExampleResponse = "0x0")]
        Task<ResultWrapper<long?>> eth_blockNumber();
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns account balance", 
            IsSharable = true,
            ExampleResponse = "0x0")]
        Task<ResultWrapper<UInt256?>> eth_getBalance([JsonRpcParameter(ExampleValue = "[\"0xaa492bb3391eeb2dbed088ac952cffa23e614b28\"]")] Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns storage data at address. storage_index", 
            IsSharable = true, 
            ExampleResponse = "0x")]
        ResultWrapper<byte[]> eth_getStorageAt([JsonRpcParameter(ExampleValue = "[\"0xc449a3cda8d0df64a3c462de8640519ad6f61a09\",\"0x1\"]")] Address address, UInt256 positionIndex, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns account nonce (number of trnsactions from the account since genesis) at the given block number", 
            IsSharable = true,
            ExampleResponse = "0x0")]
        Task<ResultWrapper<UInt256?>> eth_getTransactionCount([JsonRpcParameter(ExampleValue = "[\"0xc449a3cda8d0df64a3c462de8640519ad6f61a09\"]")] Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns number of transactions in the block block hash", 
            IsSharable = true, 
            ExampleResponse = "0xb")]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(
            [JsonRpcParameter(ExampleValue = "[\"0x1b310658e0d527b77e674dc0df86f132ae83098762bde39073da10013b7f80d8\"]")] Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Returns number of transactions in the block by block number", IsSharable = true)]
        ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns number of uncles in the block by block hash", 
            IsSharable = true, 
            ExampleResponse = "0x0")]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockHash([JsonRpcParameter(ExampleValue = "[\"0xe495c3385bb9162103bc07989d7160c38759e017c37c7d0608268bd5989d6bed \"]")] Keccak blockHash);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns number of uncles in the block by block number", 
            IsSharable = true, 
            ExampleResponse = "0x0")]
        ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber([JsonRpcParameter(ExampleValue = "[\"5127400\"]")] BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns account code at given address and block", 
            IsSharable = true,
            ExampleResponse = "0x")]
        ResultWrapper<byte[]> eth_getCode([JsonRpcParameter(ExampleValue = "[\"0xaa492bb3391eeb2dbed088ac952cffa23e614b28\"]")] Address address, BlockParameter blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = false, Description = "Signs a transaction", IsSharable = true)]
        ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a transaction to the tx pool and broadcasting", IsSharable = true)]
        Task<ResultWrapper<Keccak>> eth_sendTransaction(TransactionForRpc rpcTx);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Send a raw transaction to the tx pool and broadcasting", IsSharable = true)]
        Task<ResultWrapper<Keccak>> eth_sendRawTransaction(byte[] transaction);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call (does not create a transaction)", IsSharable = false)]
        ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter? blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Executes a tx call and returns gas used (does not create a transaction)", IsSharable = false)]
        ResultWrapper<UInt256?> eth_estimateGas(TransactionForRpc transactionCall, BlockParameter? blockParameter = null);
        
        [JsonRpcMethod(IsImplemented = true,
            Description = "Creates an [EIP2930](https://eips.ethereum.org/EIPS/eip-2930) type AccessList for the given transaction",
            EdgeCaseHint = "If your transaction has code executed, then you can generate transaction access list with eth_createAccessList. If you send it with your transaction then it will lower your gas cost on Ethereum",
            IsSharable = false)]
        ResultWrapper<AccessListForRpc> eth_createAccessList(
            [JsonRpcParameter(Description = "Transaction's details")]
            TransactionForRpc transactionCall,
            [JsonRpcParameter(Description = "(optional)")]
            BlockParameter? blockParameter = null,
            [JsonRpcParameter(Description = "(optional)")]
            bool optimize = true);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves a block by hash", 
            IsSharable = true,
            ExampleResponse = "{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0x1\",\"extraData\":\"0x000000000000436f6e73656e5379732048797065726c656467657220426573754d3f7b71165a8266fcc569c96b6fcf9971ee4a8df59eeec4dcced0df8d778733429988e21d0124918859f988be9debf4b25fb5282ea41a2fc15f827f446ec93200\",\"gasLimit\":\"0x1c9c364\",\"gasUsed\":\"0x3aa87\",\"hash\":\"0xf33507f93a046dbdbb80dee5f47b84283297f6c53f1b665adc3cb6fe4138aa84\",\"logsBloom\":\"0x00000000000020000000000008000060000000000000000000000000000000000000000000000000201000020008000000000000000000000100000000200020000000000000000000000008000000000000000010000000000000000000000000000000000000000000080000000000000000000000002000000010000000000000000000000000000000000000000000040000001000000000000000020000020400000000000000000000000000000000000000000000000000010000000000000002080000000000000000020000000000000000000000000000000000000010020000000000000000000000000100000000000000000000010000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x4e3d79\",\"parentHash\":\"0x01dba3a7eb61dc6dba3f9663c8fb632f76f60a476f57df74c3e5bd9d0a246339\",\"receiptsRoot\":\"0x70f3bd929735d8edeb953cd30a27e703e7dd3ec4af32cb74fe8ac302f9e7fb87\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x754\",\"stateRoot\":\"0x71af7e25302d1baa4c988c267450eb2c7fa20938fac377809c8d77f8ff8108ac\",\"totalDifficulty\":\"0x726275\",\"timestamp\":\"0x60ec1218\",\"baseFeePerGas\":\"0x7\",\"transactions\":[\"0xa65d391d8149ed0906fab923e870d2bc7f6d27c2be10fe1bcfc6f02869b38ef3\",\"0x369a89354041b7a8cb40edce51c36ebb0ee6ffa4d8056f5a658d90f3bbe1a81a\",\"0xf857daf60d03381b9a6ecb341b62798b424d20dc05763858e13955dd866b489d\"],\"transactionsRoot\":\"0x90115f8dc10c08e748675f52f3904615729a014461ca80d72c60239bf75ee209\",\"uncles\":[]}")]
        ResultWrapper<BlockForRpc> eth_getBlockByHash([JsonRpcParameter(ExampleValue = "[\"0xf33507f93a046dbdbb80dee5f47b84283297f6c53f1b665adc3cb6fe4138aa84\"]")] Keccak blockHash, bool returnFullTransactionObjects = false);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves a block by number", 
            IsSharable = true,
            ExampleResponse = "{\"author\":\"0x0000000000000000000000000000000000000000\",\"difficulty\":\"0x1\",\"extraData\":\"0x000000000000436f6e73656e5379732048797065726c656467657220426573754d3f7b71165a8266fcc569c96b6fcf9971ee4a8df59eeec4dcced0df8d778733429988e21d0124918859f988be9debf4b25fb5282ea41a2fc15f827f446ec93200\",\"gasLimit\":\"0x1c9c364\",\"gasUsed\":\"0x3aa87\",\"hash\":\"0xf33507f93a046dbdbb80dee5f47b84283297f6c53f1b665adc3cb6fe4138aa84\",\"logsBloom\":\"0x00000000000020000000000008000060000000000000000000000000000000000000000000000000201000020008000000000000000000000100000000200020000000000000000000000008000000000000000010000000000000000000000000000000000000000000080000000000000000000000002000000010000000000000000000000000000000000000000000040000001000000000000000020000020400000000000000000000000000000000000000000000000000010000000000000002080000000000000000020000000000000000000000000000000000000010020000000000000000000000000100000000000000000000010000000000\",\"miner\":\"0x0000000000000000000000000000000000000000\",\"mixHash\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"nonce\":\"0x0000000000000000\",\"number\":\"0x4e3d79\",\"parentHash\":\"0x01dba3a7eb61dc6dba3f9663c8fb632f76f60a476f57df74c3e5bd9d0a246339\",\"receiptsRoot\":\"0x70f3bd929735d8edeb953cd30a27e703e7dd3ec4af32cb74fe8ac302f9e7fb87\",\"sha3Uncles\":\"0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347\",\"size\":\"0x754\",\"stateRoot\":\"0x71af7e25302d1baa4c988c267450eb2c7fa20938fac377809c8d77f8ff8108ac\",\"totalDifficulty\":\"0x726275\",\"timestamp\":\"0x60ec1218\",\"baseFeePerGas\":\"0x7\",\"transactions\":[\"0xa65d391d8149ed0906fab923e870d2bc7f6d27c2be10fe1bcfc6f02869b38ef3\",\"0x369a89354041b7a8cb40edce51c36ebb0ee6ffa4d8056f5a658d90f3bbe1a81a\",\"0xf857daf60d03381b9a6ecb341b62798b424d20dc05763858e13955dd866b489d\"],\"transactionsRoot\":\"0x90115f8dc10c08e748675f52f3904615729a014461ca80d72c60239bf75ee209\",\"uncles\":[]}")]
        ResultWrapper<BlockForRpc> eth_getBlockByNumber([JsonRpcParameter(ExampleValue = "[\"5127545\"]")] BlockParameter blockParameter, bool returnFullTransactionObjects = false);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves a transaction by hash", 
            IsSharable = true, 
            ExampleResponse = "{\"hash\":\"0xabca23910646013d608ec671de099447ab60b2b7159ad8319c3c088e8d9ea0fa\",\"nonce\":\"0x1a\",\"blockHash\":\"0xcb6756f69e0469acd5e5bb77966be580786ec2c11de85c9ddfd75257010e34f8\",\"blockNumber\":\"0x4dfbc7\",\"transactionIndex\":\"0xb\",\"from\":\"0xe1e7ab1c643dbe5b24739fdf2a5c7c193b54dd99\",\"to\":\"0x0b10e304088b2ba2b2acfd2f72573faad31a13a5\",\"value\":\"0x0\",\"gasPrice\":\"0x2540be400\",\"gas\":\"0xb4a4\",\"data\":\"0x095ea7b300000000000000000000000092c1576845703089cf6c0788379ed81f75f45dd500000000000000000000000000000000000000000000000000000002540be400\",\"input\":\"0x095ea7b300000000000000000000000092c1576845703089cf6c0788379ed81f75f45dd500000000000000000000000000000000000000000000000000000002540be400\",\"type\":\"0x0\",\"v\":\"0x2d\",\"s\":\"0x496d72d435ead8a8a9a865b14d6a102c1a9f848681d050dbbf11c522c612235\",\"r\":\"0xc8350e831203fecc8bff41f5cf858ac1d121e4b4d9e59c1137cc9440516ca9fd\"}")]
        Task<ResultWrapper<TransactionForRpc>> eth_getTransactionByHash(
            [JsonRpcParameter(ExampleValue = "\"0xabca23910646013d608ec671de099447ab60b2b7159ad8319c3c088e8d9ea0fa\"")] Keccak transactionHash);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Returns the pending transactions list",
            IsSharable = true, 
            ExampleResponse = "[]")]
        ResultWrapper<TransactionForRpc[]> eth_pendingTransactions();
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves a transaction by block hash and index", 
            IsSharable = true,
            ExampleResponse = "{\"hash\":\"0xb87ec4c8cb36a06f49cdd93c2e9f63e0b7db9af07a605c8bcf1fbe705162344e\",\"nonce\":\"0x5d\",\"blockHash\":\"0xfe47fb3539ccce9d19a032473effdd6ce19e3c921bbae2746152ccf82ceef48e\",\"blockNumber\":\"0x4dfc90\",\"transactionIndex\":\"0x2\",\"from\":\"0xaa9a0f962e433755c843175488fe088fccf8526f\",\"to\":\"0x074b24cef703f17fe123fa1b82081055775b7004\",\"value\":\"0x0\",\"gasPrice\":\"0x2540be401\",\"gas\":\"0x130ab\",\"data\":\"0x428dc451000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000005d3c0f4ca5ee99f8e8f59ff9a5fab04f6a7e007f0000000000000000000000009d233a907e065855d2a9c7d4b552ea27fb2e5a36000000000000000000000000cbe56b00d173a26a5978ce90db2e33622fd95a28\",\"input\":\"0x428dc451000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000005d3c0f4ca5ee99f8e8f59ff9a5fab04f6a7e007f0000000000000000000000009d233a907e065855d2a9c7d4b552ea27fb2e5a36000000000000000000000000cbe56b00d173a26a5978ce90db2e33622fd95a28\",\"type\":\"0x0\",\"v\":\"0x2e\",\"s\":\"0x696f6db060a6dd30435a7f592506ba3213f81cf4704e211a1a45a99f8984189a\",\"r\":\"0x7e07076186e38b68cb7e4f68a04258a5744c5a2ad1a7153456ee662a07902954\"}")]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(
            [JsonRpcParameter(ExampleValue = "[\"0xfe47fb3539ccce9d19a032473effdd6ce19e3c921bbae2746152ccf82ceef48e\",\"0x2\"]")] Keccak blockHash, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves a transaction by block number and index", 
            IsSharable = true, 
            ExampleResponse = "{\"hash\":\"0xfd320a4949990929f64b52041c58a74c8ce13289b3d6853bd8073b0580aa031a\",\"nonce\":\"0x5b\",\"blockHash\":\"0xd779e1a5ce8f34544d66d219bb3e5331a7b280fae89a36d7d52813a23e1ca1e3\",\"blockNumber\":\"0x4dfdd8\",\"transactionIndex\":\"0x8\",\"from\":\"0xadb540569e2db497bd973c141b0b63be98461e40\",\"to\":\"0x074b24cef703f17fe123fa1b82081055775b7004\",\"value\":\"0x0\",\"gasPrice\":\"0x12a05f200\",\"gas\":\"0x927c0\",\"data\":\"0x428dc451000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000005d3c0f4ca5ee99f8e8f59ff9a5fab04f6a7e007f0000000000000000000000009d233a907e065855d2a9c7d4b552ea27fb2e5a36000000000000000000000000cbe56b00d173a26a5978ce90db2e33622fd95a28\",\"input\":\"0x428dc451000000000000000000000000000000000000000000000000000000000000002000000000000000000000000000000000000000000000000000000000000000030000000000000000000000005d3c0f4ca5ee99f8e8f59ff9a5fab04f6a7e007f0000000000000000000000009d233a907e065855d2a9c7d4b552ea27fb2e5a36000000000000000000000000cbe56b00d173a26a5978ce90db2e33622fd95a28\",\"type\":\"0x0\",\"v\":\"0x2e\",\"s\":\"0x37b90a929884787df717c87258f0434e2f115ce2fbb4bfc230322112fa9d5bbc\",\"r\":\"0x5222eff9e16b5c3e9e8901d9c45fc8e0f9cf774e8a56546a504025ef67ceefec\"}")]
        ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(
        [JsonRpcParameter(ExampleValue = "[\"5111256\",\"0x8\"]" )] BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, 
        Description = "Retrieves a transaction receipt by tx hash", 
        IsSharable = true,
        ExampleResponse = "{\"transactionHash\":\"0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71\",\"transactionIndex\":\"0x7\",\"blockHash\":\"0x42def051b21038905cd2a2bc28d460a94df2249466847f0e1bcb4be4eb21891a\",\"blockNumber\":\"0x4e3f39\",\"cumulativeGasUsed\":\"0x62c9d\",\"gasUsed\":\"0xe384\",\"effectiveGasPrice\":\"0x12a05f200\",\"from\":\"0x0afe0a94415e8974052e7e6cfab19ee1c2ef4f69\",\"to\":\"0x19e8c84d4943e58b035626b064cfc76ee13ee6cb\",\"contractAddress\":null,\"logs\":[{\"removed\":false,\"logIndex\":\"0x0\",\"transactionIndex\":\"0x7\",\"transactionHash\":\"0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71\",\"blockHash\":\"0x42def051b21038905cd2a2bc28d460a94df2249466847f0e1bcb4be4eb21891a\",\"blockNumber\":\"0x4e3f39\",\"address\":\"0x2ac3c1d3e24b45c6c310534bc2dd84b5ed576335\",\"data\":\"0x0000000000000000000000000000000000000000000000000000000000000000\",\"topics\":[\"0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef\",\"0x00000000000000000000000019e8c84d4943e58b035626b064cfc76ee13ee6cb\",\"0x00000000000000000000000028078300a459a9e136f872285654cdc74463041e\"]},{\"removed\":false,\"logIndex\":\"0x1\",\"transactionIndex\":\"0x7\",\"transactionHash\":\"0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71\",\"blockHash\":\"0x42def051b21038905cd2a2bc28d460a94df2249466847f0e1bcb4be4eb21891a\",\"blockNumber\":\"0x4e3f39\",\"address\":\"0x19e8c84d4943e58b035626b064cfc76ee13ee6cb\",\"data\":\"0x000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000007735940000000000000000000000000000000000000000000000000000000000000000000\",\"topics\":[\"0x950494fc3642fae5221b6c32e0e45765c95ebb382a04a71b160db0843e74c99f\",\"0x0000000000000000000000000afe0a94415e8974052e7e6cfab19ee1c2ef4f69\",\"0x00000000000000000000000028078300a459a9e136f872285654cdc74463041e\",\"0x0000000000000000000000000afe0a94415e8974052e7e6cfab19ee1c2ef4f69\"]}],\"logsBloom\":\"0x00000000000000000000000000000000000000000000000020000000000000800000000000000000000400000000000000000000000000000000000000002000000000000000000000000008000000000000000000000000000000000000000000000002002000000000000000000000000000000000000000000812000000000000000000000000000001000000000000000000000008000400008000000000000000000000000000000000000000000000000000000000800000000000000000000002000000000000000000000000000000000000100000000000000000002000000000000000000000000010000000000000000000000400000000020000\",\"status\":\"0x1\",\"type\":\"0x0\"}")]
        Task<ResultWrapper<GetTransactionReceiptResponse>> eth_getTransactionReceipt([JsonRpcParameter(ExampleValue = "[\"0x80757153e93d1b475e203406727b62a501187f63e23b8fa999279e219ee3be71\"]")] Keccak txHashData);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Retrieves an uncle block header by block hash and uncle index", 
            IsSharable = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Retrieves an uncle block header by block number and uncle index", IsSharable = true)]
        ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, UInt256 positionIndex);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsSharable = false)]
        ResultWrapper<UInt256?> eth_newFilter(Filter filter);
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Creates an update filter", 
            IsSharable = false, 
            ExampleResponse = "0x0")]
        ResultWrapper<UInt256?> eth_newBlockFilter();
        
        [JsonRpcMethod(IsImplemented = true, 
            Description = "Creates an update filter", 
            IsSharable = false, 
            ExampleResponse = "0x1")]
        ResultWrapper<UInt256?> eth_newPendingTransactionFilter();
        
        [JsonRpcMethod(IsImplemented = true, Description = "Creates an update filter", IsSharable = false)]
        ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", IsSharable = true)]
        ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads filter changes", IsSharable = true)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId);
        
        [JsonRpcMethod(IsImplemented = true, Description = "Reads logs", IsSharable = false)]
        ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = true)]
        ResultWrapper<IEnumerable<byte[]>> eth_getWork();
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = false)]
        ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsSharable = false)]
        ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id);
        
        [JsonRpcMethod(Description = "https://github.com/ethereum/EIPs/issues/1186", IsImplemented = true, IsSharable = true)]
        ResultWrapper<AccountProof> eth_getProof(Address accountAddress, byte[][] hashRate, BlockParameter blockParameter);
    }
}
