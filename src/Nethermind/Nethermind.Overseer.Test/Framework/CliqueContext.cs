// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Overseer.Test.JsonRpc;

namespace Nethermind.Overseer.Test.Framework;

public class CliqueContext(CliqueState state) : TestContextBase<CliqueContext, CliqueState>(state)
{
    public CliqueContext Propose(Address address, bool vote)
    {
        IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
        return AddJsonRpc($"vote {vote} for {address}", "clique_propose",
            () => client.PostAsync<string>("clique_propose", [address, vote]));
    }

    public CliqueContext Discard(Address address)
    {
        IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
        return AddJsonRpc($"discard vote for {address}", "clique_discard",
            () => client.PostAsync<string>("clique_discard", [address]));
    }

    public CliqueContext SendTransaction(TransactionForRpc tx)
    {
        IJsonRpcClient client = TestBuilder.CurrentNode.JsonRpcClient;
        return AddJsonRpc($"send tx to {TestBuilder.CurrentNode.HttpPort}", "eth_sendTransaction",
            () => client.PostAsync<string>("eth_SendTransaction", [tx]));
    }
}
