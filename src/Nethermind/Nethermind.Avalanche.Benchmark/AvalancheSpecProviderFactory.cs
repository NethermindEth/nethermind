// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Avalanche;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Avalanche.Benchmark;

/// <summary>
/// Builds the Avalanche C-Chain <see cref="ISpecProvider"/> (chain id 43114) from a chainspec
/// JSON file, reusing Nethermind's <see cref="ChainSpecLoader"/> and the production
/// <see cref="AvalancheChainSpecBasedSpecProvider"/>.
/// </summary>
/// <remarks>
/// The chainspec JSON declares <c>"engine": { "avalanche": { "params": { ... } } }</c>. Nethermind's
/// <see cref="ChainSpecParametersProvider"/> deserializes those params into an
/// <see cref="AvalancheChainSpecEngineParameters"/> instance by discovering the
/// <c>IChainSpecEngineParameters</c> implementation whose engine name is <c>avalanche</c>. The
/// per-fork EIP flags are then derived in <see cref="AvalancheChainSpecBasedSpecProvider"/> from
/// the activation block numbers (Apricot Phase 2/3) and timestamps (Durango/Etna/Fortuna/Granite).
/// </remarks>
public static class AvalancheSpecProviderFactory
{
    /// <summary>
    /// Loads the chainspec at <paramref name="chainSpecPath"/> and constructs the Avalanche spec provider.
    /// </summary>
    /// <param name="chainSpecPath">Path to an Avalanche C-Chain chainspec JSON file.</param>
    /// <returns>The loaded <see cref="ChainSpec"/> and the Avalanche-aware <see cref="ISpecProvider"/>.</returns>
    public static (ChainSpec ChainSpec, ISpecProvider SpecProvider) Create(string chainSpecPath)
    {
        if (!File.Exists(chainSpecPath))
        {
            throw new FileNotFoundException($"Avalanche chainspec not found at '{chainSpecPath}'.", chainSpecPath);
        }

        ChainSpecLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        ChainSpec chainSpec;
        using (FileStream stream = File.OpenRead(chainSpecPath))
        {
            chainSpec = loader.Load(stream);
        }

        AvalancheChainSpecEngineParameters engineParameters =
            chainSpec.EngineChainSpecParametersProvider.GetChainSpecParameters<AvalancheChainSpecEngineParameters>();

        ISpecProvider specProvider =
            new AvalancheChainSpecBasedSpecProvider(chainSpec, engineParameters, LimboLogs.Instance);

        return (chainSpec, specProvider);
    }
}
