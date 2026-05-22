// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class ReceiptJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : JsonRpcDataSource<ReceiptForRpc>(uri, serializer), IConsensusDataSource<ReceiptForRpc>, IConsensusDataSourceWithParameter<Hash256>
        {
            public Hash256 Parameter { get; set; } = null!;

            public override async Task<string> GetJsonData() =>
                await SendRequest(CreateRequest("eth_getTransactionReceipt", Parameter.ToString()));
        }
    }
}
