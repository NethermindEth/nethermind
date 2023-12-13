// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class GethLikeTxTraceJsonRpcDataSource : JsonRpcDataSource<GethLikeTxTrace>,
            IConsensusDataSource<GethLikeTxTrace>,
            IConsensusDataSourceWithParameter<Hash256>,
            IConsensusDataSourceWithParameter<GethTraceOptions>
        {
            private Hash256 _transactionHash = null!;
            private GethTraceOptions _options = null!;

            public GethLikeTxTraceJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            Hash256 IConsensusDataSourceWithParameter<Hash256>.Parameter
            {
                get => _transactionHash;
                set => _transactionHash = value;
            }

            GethTraceOptions IConsensusDataSourceWithParameter<GethTraceOptions>.Parameter
            {
                get => _options;
                set => _options = value;
            }

            public override async Task<string> GetJsonData() =>
                await SendRequest(CreateRequest("debug_traceTransaction", _transactionHash.ToString(), _serializer.Serialize(_options)));
        }
    }
}
