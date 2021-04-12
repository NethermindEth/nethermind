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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Test
{
    public partial class ConsensusHelperTests
    {
        private class GethLikeTxTraceJsonRpcDataSource : JsonRpcDataSource<GethLikeTxTrace>,
            IConsensusDataSource<GethLikeTxTrace>,
            IConsensusDataSourceWithParameter<Keccak>, 
            IConsensusDataSourceWithParameter<GethTraceOptions>
        {
            private Keccak _transactionHash;
            private GethTraceOptions _options;

            public GethLikeTxTraceJsonRpcDataSource(Uri uri, IJsonSerializer serializer) : base(uri, serializer)
            {
            }

            Keccak IConsensusDataSourceWithParameter<Keccak>.Parameter
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
