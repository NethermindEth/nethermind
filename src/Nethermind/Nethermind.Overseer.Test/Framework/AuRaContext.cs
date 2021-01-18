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

using System;
using Nethermind.Overseer.Test.JsonRpc;
using Newtonsoft.Json.Linq;

namespace Nethermind.Overseer.Test.Framework
{
    public class AuRaContext : TestContextBase<AuRaContext, AuRaState>
    {
        public AuRaContext(AuRaState state) : base(state)
        {
        }

        public AuRaContext ReadBlockAuthors()
        {
            for (int i = 1; i <= State.BlocksCount; i++)
            {
                ReadBlockAuthor(i);
            }
            
            return this;
        }
        
        public AuRaContext ReadBlockNumber()
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc("Read block number", "eth_blockNumber",
                () => client.PostAsync<long>("eth_blockNumber"), stateUpdater: (s, r) => s.BlocksCount = r.Result
            );
        }
        
        private AuRaContext ReadBlockAuthor(long blockNumber)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc("Read block", "eth_getBlockByNumber",
                () => client.PostAsync<JObject>("eth_getBlockByNumber", new object[] {blockNumber, false}),
                stateUpdater: (s, r) => s.Blocks.Add(
                    Convert.ToInt64(r.Result["number"].Value<string>(), 16), 
                    (r.Result["miner"].Value<string>(), Convert.ToInt64(r.Result["step"].Value<string>(), 16))));
        }
    }
}
