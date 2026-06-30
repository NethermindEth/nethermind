// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.Avalanche.Genesis;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Avalanche.Test.Genesis;

/// <summary>
/// Verifies that <see cref="AvalancheCChainGenesis"/> reconstructs the real Avalanche mainnet C-Chain genesis
/// (block 0) byte-exactly from the authoritative <c>cChainGenesis</c> JSON (embedded fixture), reproducing both
/// its state root and its block hash.
/// </summary>
/// <remarks>
/// The mainnet C-Chain genesis allocates a single account (<c>0x0100…0000</c>, the native-asset-call precompile
/// stub, balance 0 + bytecode). It must be encoded with Coreth's 5-field <c>isMultiCoin</c> account RLP for the
/// state root to match, and the genesis header is the 16-field shape with a zero <c>ExtDataHash</c>.
/// Targets: chainId 43114, stateRoot <c>0xd65eb1b8…29cc</c>, block hash <c>0x31ced5b9…96b</c>.
/// </remarks>
public class AvalancheGenesisParityTests
{
    private const string GenesisStateRoot = "0xd65eb1b8604a7aa497d41cd6372663785a5f809a17bd192edb86658ef24e29cc";
    private const string GenesisBlockHash = "0x31ced5b9beb7f8782b014660da0cb18cc409f121f408186886e1ca3e8eeca96b";

    private static AvalancheCChainGenesis LoadMainnetGenesis()
    {
        Assembly assembly = typeof(AvalancheGenesisParityTests).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .Single(n => n.Contains("cchain-genesis", StringComparison.OrdinalIgnoreCase));

        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("embedded mainnet genesis fixture not found");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return AvalancheCChainGenesis.FromJson(buffer.ToArray());
    }

    [Test]
    public void Genesis_state_root_matches_mainnet()
    {
        AvalancheCChainGenesis genesis = LoadMainnetGenesis();
        Assert.That(genesis.StateRoot, Is.EqualTo(new Hash256(GenesisStateRoot)));
    }

    [Test]
    public void Genesis_block_hash_matches_mainnet()
    {
        AvalancheCChainGenesis genesis = LoadMainnetGenesis();
        Assert.That(genesis.Hash, Is.EqualTo(new Hash256(GenesisBlockHash)));
    }

    [Test]
    public void Genesis_chain_id_is_43114()
    {
        AvalancheCChainGenesis genesis = LoadMainnetGenesis();
        Assert.That(genesis.ChainId, Is.EqualTo(43114));
    }
}
