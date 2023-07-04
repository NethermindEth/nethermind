// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
                () => client.PostAsync<JObject>("eth_getBlockByNumber", new object[] { blockNumber, false }),
                stateUpdater: (s, r) => s.Blocks.Add(
                    Convert.ToInt64(r.Result["number"].Value<string>(), 16),
                    (r.Result["miner"].Value<string>(), Convert.ToInt64(r.Result["step"].Value<string>(), 16))));
        }
    }
}
