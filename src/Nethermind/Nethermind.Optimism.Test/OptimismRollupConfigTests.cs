// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nethermind.Config;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.Cl.Rpc;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Optimism.Test;

public class OptimismRollupConfigTests
{
    private const string ResourcePrefix = "Nethermind.Config.chainspec.";

    private static string[] EmbeddedOptimismChainSpecs()
    {
        string[] chainSpecPaths = typeof(IConfig).Assembly.GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Where(static name => name.EndsWith(".json.zst", StringComparison.Ordinal))
            .Select(static name => $"chainspec/{name[ResourcePrefix.Length..]}")
            .ToArray();
        return chainSpecPaths.Length > 0
            ? chainSpecPaths
            : throw new InvalidOperationException("No embedded Optimism chain specs were found.");
    }

    [Test]
    [NonParallelizable]
    public void Embedded_optimism_chain_specs_build_rollup_configs()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") == "0")
        {
            Assert.Ignore("Catalog validation runs in normal and checked variants; Zstd decompression exceeds the no-intrinsics job budget.");
        }

        EthereumJsonSerializer serializer = new();
        foreach (string chainSpecPath in EmbeddedOptimismChainSpecs())
        {
            string resourceName = ResourcePrefix + chainSpecPath["chainspec/".Length..];
            using Stream stream = typeof(IConfig).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded chain spec {resourceName} was not found.");
            RollupConfigInputLoader inputLoader = new(serializer);
            ChainSpec chainSpec = new ZstdChainSpecLoader(inputLoader).Load(stream);

            OptimismRollupConfig config = OptimismRollupConfig.Build(
                inputLoader.ClParameters,
                inputLoader.OptimismParameters,
                chainSpec);

            Assert.That(config.L2ChainID, Is.EqualTo(chainSpec.ChainId), chainSpecPath);
        }
    }

    [Test]
    public void Op_mainnet_rollup_config_uses_rollup_genesis_and_contract_fields()
    {
        ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance)
            .LoadEmbeddedOrFromFile("chainspec/op-mainnet.json.zst");
        CLChainSpecEngineParameters clParameters = chainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<CLChainSpecEngineParameters>();
        OptimismChainSpecEngineParameters engineParameters = chainSpec.EngineChainSpecParametersProvider
            .GetChainSpecParameters<OptimismChainSpecEngineParameters>();

        OptimismRollupConfig config = OptimismRollupConfig.Build(clParameters, engineParameters, chainSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(config.Genesis.L1.Number, Is.EqualTo(clParameters.L1GenesisNumber));
            Assert.That(config.Genesis.L2.Number, Is.EqualTo(chainSpec.Genesis?.Number));
            Assert.That(config.BatchInboxAddress, Is.EqualTo(clParameters.BatcherInboxAddress));
            Assert.That(config.DepositContractAddress, Is.EqualTo(clParameters.OptimismPortalProxy));
        }
    }

    private sealed class RollupConfigInputLoader(IJsonSerializer serializer) : IChainSpecLoader
    {
        private CLChainSpecEngineParameters? _clParameters;
        private OptimismChainSpecEngineParameters? _optimismParameters;

        public CLChainSpecEngineParameters ClParameters => _clParameters
            ?? throw new InvalidOperationException("Optimism CL parameters were not loaded.");

        public OptimismChainSpecEngineParameters OptimismParameters => _optimismParameters
            ?? throw new InvalidOperationException("Optimism parameters were not loaded.");

        public ChainSpec Load(Stream streamData)
        {
            ChainSpecJson chainSpecJson = serializer.Deserialize<ChainSpecJson>(streamData)
                ?? throw new InvalidDataException("Embedded chain spec is empty.");
            ChainSpecParamsJson parameters = chainSpecJson.Params
                ?? throw new InvalidDataException("Embedded chain spec parameters are missing.");
            ChainSpecGenesisJson genesis = chainSpecJson.Genesis
                ?? throw new InvalidDataException("Embedded chain spec genesis is missing.");
            ChainSpecJson.EngineJson engine = chainSpecJson.Engine
                ?? throw new InvalidDataException("Embedded chain spec engine is missing.");

            _clParameters = DeserializeEngineParameters<CLChainSpecEngineParameters>(engine, "OptimismCL");
            _optimismParameters = DeserializeEngineParameters<OptimismChainSpecEngineParameters>(engine, "Optimism");

            return new ChainSpec
            {
                ChainId = parameters.ChainId ?? parameters.NetworkId ?? 1,
                Parameters = new ChainParameters
                {
                    Eip1559ElasticityMultiplier = parameters.Eip1559ElasticityMultiplier,
                    Eip1559BaseFeeMaxChangeDenominator = parameters.Eip1559BaseFeeMaxChangeDenominator
                },
                Genesis = Build.A.Block
                    .WithHeader(Build.A.BlockHeader.WithNumber(0).WithTimestamp(genesis.Timestamp).TestObject)
                    .TestObject
            };
        }

        private T DeserializeEngineParameters<T>(ChainSpecJson.EngineJson engine, string engineName)
        {
            if (!engine.CustomEngineData.TryGetValue(engineName, out JsonElement engineJson))
            {
                throw new InvalidDataException($"Embedded chain spec engine {engineName} is missing.");
            }

            JsonElement parametersJson = engineJson.TryGetProperty("params", out JsonElement nestedParameters)
                ? nestedParameters
                : engineJson;
            return serializer.Deserialize<T>(parametersJson.GetRawText())
                ?? throw new InvalidDataException($"Embedded chain spec engine {engineName} is empty.");
        }
    }
}
