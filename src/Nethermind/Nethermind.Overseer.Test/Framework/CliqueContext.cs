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

using Nethermind.Core;
using Nethermind.JsonRpc.Data;
using Nethermind.Overseer.Test.JsonRpc;

namespace Nethermind.Overseer.Test.Framework
{
    public class CliqueContext : TestContextBase<CliqueContext, CliqueState>
    {
        public CliqueContext(CliqueState state) : base(state)
        {
        }

        public CliqueContext Propose(Address address, bool vote)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc($"vote {vote} for {address}", "clique_propose",
                () => client.PostAsync<string>("clique_propose", new object[] {address, vote}));
        }

        public CliqueContext Discard(Address address)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc($"discard vote for {address}", "clique_discard",
                () => client.PostAsync<string>("clique_discard", new object[] {address}));
        }

        public CliqueContext SendTransaction(TransactionForRpc tx)
        {
            IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
            return AddJsonRpc($"send tx to {TestBuilder.CurrentNode.HttpPort}", "eth_sendTransaction",
                () => client.PostAsync<string>("eth_SendTransaction", new object[] {tx}));
        }
    }
}
