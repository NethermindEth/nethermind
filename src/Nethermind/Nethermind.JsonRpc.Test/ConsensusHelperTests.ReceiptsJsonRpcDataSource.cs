// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class ReceiptsJsonRpcDataSource : JsonRpcDataSource<IEnumerable<ReceiptForRpc>>, IConsensusDataSource<IEnumerable<ReceiptForRpc>>, IConsensusDataSourceWithParameter<Keccak>
        {
            public ReceiptsJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            public Keccak Parameter { get; set; } = null!;

            public override async Task<string> GetJsonData() => GetJson(await GetJsonDatas());

            private string GetJson(IEnumerable<string> jsons) => $"[{string.Join(',', jsons)}]";

            private async Task<IEnumerable<string>> GetJsonDatas()
            {
                JsonRpcRequest request = CreateRequest("eth_getBlockByHash", Parameter.ToString(), false);
                string blockJson = await SendRequest(request);
                BlockForRpcTxHashes block = _serializer.Deserialize<JsonRpcSuccessResponse<BlockForRpcTxHashes>>(blockJson).Result;
                List<string> transactionsJsons = new(block.Transactions.Length);
                foreach (string tx in block.Transactions)
                {
                    transactionsJsons.Add(await SendRequest(CreateRequest("eth_getTransactionReceipt", tx)));
                }

                return transactionsJsons;
            }

            public override async Task<(IEnumerable<ReceiptForRpc>, string)> GetData()
            {
                IEnumerable<string> receiptJsons = (await GetJsonDatas()).ToArray();
                return (receiptJsons.Select(j => _serializer.Deserialize<JsonRpcSuccessResponse<ReceiptForRpc>>(j).Result), GetJson(receiptJsons));
            }

            private class BlockForRpcTxHashes : BlockForRpc
            {
                public new string[] Transactions { get; set; }
            }
        }
    }
}
