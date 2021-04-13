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
// 

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
