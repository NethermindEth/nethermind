﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

            public Keccak Parameter { get; set; }
            
            public override async Task<string> GetJsonData() => GetJson(await GetJsonDatas());

            private string GetJson(IEnumerable<string> jsons) => $"[{string.Join(',', jsons)}]";

            private async Task<IEnumerable<string>> GetJsonDatas()
            {
                JsonRpcRequest request = CreateRequest("eth_getBlockByHash", Parameter.ToString(), false);
                string blockJson = await SendRequest(request);
                BlockForRpcTxHashes block = _serializer.Deserialize<JsonRpcSuccessResponse<BlockForRpcTxHashes>>(blockJson).Result;
                List<string> transactionsJsons = new List<string>(block.Transactions.Length);
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
