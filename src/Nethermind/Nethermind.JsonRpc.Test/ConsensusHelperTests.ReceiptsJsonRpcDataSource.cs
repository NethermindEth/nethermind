// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class ReceiptsJsonRpcDataSource : JsonRpcDataSource<IEnumerable<ReceiptForRpc>>, IConsensusDataSource<IEnumerable<ReceiptForRpc>>, IConsensusDataSourceWithParameter<Hash256>
        {
            public ReceiptsJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            public Hash256 Parameter { get; set; } = null!;

            public override async Task<string> GetJsonData() => GetJson(await GetJsonPayloads());

            private string GetJson(IEnumerable<string> jsonItems) => $"[{string.Join(',', jsonItems)}]";

            private async Task<IEnumerable<string>> GetJsonPayloads()
            {
                JsonRpcRequest request = CreateRequest("eth_getBlockByHash", Parameter.ToString(), false);
                string blockJson = await SendRequest(request);
                BlockForRpcTxHashes block = _serializer.Deserialize<JsonRpcSuccessResponse<BlockForRpcTxHashes>>(blockJson).Result;
                List<string> transactionJsonPayloads = new(block.Transactions!.Length);
                foreach (string tx in block.Transactions)
                {
                    transactionJsonPayloads.Add(await SendRequest(CreateRequest("eth_getTransactionReceipt", tx)));
                }

                return transactionJsonPayloads;
            }

            public override async Task<(IEnumerable<ReceiptForRpc>, string)> GetData()
            {
                IEnumerable<string> receiptJsonPayloads = (await GetJsonPayloads()).ToArray();
                return (receiptJsonPayloads.Select(j => _serializer.Deserialize<JsonRpcSuccessResponse<ReceiptForRpc>>(j).Result), GetJson(receiptJsonPayloads));
            }

            private class BlockForRpcTxHashes : BlockForRpc
            {
                public new string[]? Transactions { get; set; }
            }
        }
    }
}
