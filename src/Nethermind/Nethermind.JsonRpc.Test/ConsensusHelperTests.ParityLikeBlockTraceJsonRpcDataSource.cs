// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class ParityLikeBlockTraceJsonRpcDataSource : JsonRpcDataSource<IEnumerable<ParityTxTraceFromStore>>,
            IConsensusDataSource<IEnumerable<ParityTxTraceFromStore>>,
            IConsensusDataSourceWithParameter<long>
        {
            public ParityLikeBlockTraceJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            public override async Task<string> GetJsonData() =>
                await SendRequest(CreateRequest("trace_block", Parameter.ToHexString(true)));

            public long Parameter { get; set; }
        }
    }
}
