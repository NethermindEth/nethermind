// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.History.Changesets;

namespace Nethermind.Init.Steps;

public sealed class TransactionIndexGenesisBootstrap(
    ChainSpec chainSpec,
    IInitConfig config,
    IJsonSerializer serializer,
    ILogManager logs)
{
    public bool TryImport(BulkFillSession session, CancellationToken token)
    {
        if (session.IsReady) return true;
        if (session.CurrentState.BlockNumber != 0) return false;
        token.ThrowIfCancellationRequested();
        Dictionary<Address, ChainSpecAllocation>? allocations = chainSpec.Allocations
            ?? new ChainSpecFileLoader(serializer, logs).LoadEmbeddedOrFromFile(config.ChainSpecPath).Allocations;
        if (allocations is null) return false;
        List<KeyValuePair<Address, Account>> accounts = [with(allocations.Count)];
        foreach ((Address address, ChainSpecAllocation allocation) in allocations)
        {
            token.ThrowIfCancellationRequested();
            if (allocation.Code is { Length: > 0 } || allocation.Constructor is { Length: > 0 } || allocation.Storage is { Count: > 0 })
                return false;
            accounts.Add(new(address, new Account(allocation.Nonce, allocation.Balance)));
        }
        session.ImportGenesis(accounts, token);
        ILogger logger = logs.GetClassLogger<TransactionIndexGenesisBootstrap>();
        if (logger.IsInfo) logger.Info($"Bulk transaction index genesis state verified from {accounts.Count} chain spec allocations without scanning history.");
        return true;
    }
}
