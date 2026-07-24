// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Flat.History;

namespace Nethermind.Init.Steps;

/// <summary>
/// Anchors the history floor at genesis: seeds the block-0 changeset from the chain spec allocations when the walk
/// could not capture genesis (history enabled after the genesis snapshot left memory). Without it, an allocation
/// never touched since genesis reads as absent at every height. Idempotent: skipped when block 0 is already present.
/// </summary>
[RunnerStepDependencies(
    dependencies: [typeof(InitializeBlockTree)],
    dependents: [typeof(InitializeBlockchain)]
)]
public class SeedFlatHistoryGenesis(
    ChainSpec chainSpec,
    IBlockTree blockTree,
    HistoryWriter historyWriter,
    HistoryReader historyReader,
    ILogManager logManager) : IStep
{
    private readonly ILogger _logger = logManager.GetClassLogger<SeedFlatHistoryGenesis>();

    public Task Execute(CancellationToken cancellationToken)
    {
        if (historyReader.HasHistoryForBlock(0)) return Task.CompletedTask;

        // On a genuinely fresh DB the genesis header does not exist yet — this step is ordered before
        // InitializeBlockchain, which executes genesis — and that node captures block 0 through the normal walk, so
        // there is nothing to seed. Any DB with an existing genesis header but no captured block 0 needs the seed:
        // genesis is never re-executed, so the walk alone can never connect to block 0. The header also carries the
        // state root the seeded marker must bind to (EIP-1898 match).
        if (blockTree.Genesis?.StateRoot is not { } genesisStateRoot) return Task.CompletedTask;

        Dictionary<Address, ChainSpecAllocation> allocations = chainSpec.Allocations ?? [];

        // A constructor allocation needs execution and a storage allocation needs a storage root; neither is
        // reconstructible here, so leave block 0 unavailable rather than seed a wrong genesis state.
        foreach (ChainSpecAllocation allocation in allocations.Values)
        {
            if (allocation.Constructor is { Length: > 0 } || allocation.Storage is { Count: > 0 })
            {
                if (_logger.IsWarn) _logger.Warn(
                    "Flat history genesis seeding skipped: the chain spec has constructor/storage allocations, which cannot be reconstructed outside genesis processing.");
                return Task.CompletedTask;
            }
        }

        List<KeyValuePair<Address, Account>> accounts = new(allocations.Count);
        foreach ((Address address, ChainSpecAllocation allocation) in allocations)
        {
            Account account = allocation.Code is { Length: > 0 } code
                ? new Account(allocation.Nonce, allocation.Balance, Keccak.EmptyTreeHash, Keccak.Compute(code))
                : new Account(allocation.Nonce, allocation.Balance);
            accounts.Add(new(address, account));
        }

        historyWriter.SeedGenesis(accounts, genesisStateRoot);
        if (_logger.IsInfo) _logger.Info($"Seeded flat history genesis from {accounts.Count} chain spec allocations.");
        return Task.CompletedTask;
    }
}
