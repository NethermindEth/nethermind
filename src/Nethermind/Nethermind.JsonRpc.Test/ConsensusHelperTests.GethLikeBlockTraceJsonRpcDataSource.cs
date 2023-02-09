// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class GethLikeBlockTraceJsonRpcDataSource : JsonRpcDataSource<IEnumerable<GethLikeTxTrace>>,
            IConsensusDataSource<IEnumerable<GethLikeTxTrace>>,
            IConsensusDataSourceWithParameter<Keccak>,
            IConsensusDataSourceWithParameter<GethTraceOptions>
        {
            private Keccak _blockHash = null!;
            private GethTraceOptions _options = null!;

            public GethLikeBlockTraceJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            Keccak IConsensusDataSourceWithParameter<Keccak>.Parameter
            {
                get => _blockHash;
                set => _blockHash = value;
            }

            GethTraceOptions IConsensusDataSourceWithParameter<GethTraceOptions>.Parameter
            {
                get => _options;
                set => _options = value;
            }

            public override async Task<string> GetJsonData() =>
                await SendRequest(CreateRequest("debug_traceBlockByHash", _blockHash.ToString(), _serializer.Serialize(_options)));
        }
    }
}
