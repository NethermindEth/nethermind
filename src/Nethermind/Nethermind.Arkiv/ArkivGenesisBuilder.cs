// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Arkiv;

/// <summary>
/// Block 0 carries GolemDB's root over the genesis accounts, not the account trie's. The genesis is built as usual —
/// the account trie still holds the allocation — and its header then takes GolemDB's root, which makes its hash the
/// network's.
/// </summary>
public sealed class ArkivGenesisBuilder(IGenesisBuilder inner, ArkivChain chain, ILogManager logManager) : IGenesisBuilder
{
    public Block Build()
    {
        Block genesis = inner.Build();
        Hash256 trieRoot = genesis.Header.StateRoot ?? throw new InvalidOperationException("Arkiv: the genesis has no state");
        Hash256 trieHash = genesis.Header.Hash!;
        chain.MapRoot(chain.GenesisRoot, trieRoot);
        genesis.Header.StateRoot = chain.GenesisRoot;
        genesis.Header.Hash = genesis.Header.CalculateHash();
        if (genesis.Hash != chain.GenesisHash)
        {
            throw new InvalidOperationException(
                $"Arkiv: the chain spec's genesis hashes to {genesis.Hash} with GolemDB's root, but the Arkiv genesis file to {chain.GenesisHash}. They describe different chains.");
        }
        ILogger logger = logManager.GetClassLogger<ArkivGenesisBuilder>();
        if (logger.IsInfo) logger.Info($"Arkiv genesis: state root {chain.GenesisRoot} (GolemDB), block {genesis.Hash}; the account trie's root {trieRoot} would make it {trieHash}");
        return genesis;
    }
}
