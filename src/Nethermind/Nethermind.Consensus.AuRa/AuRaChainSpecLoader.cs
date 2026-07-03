// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// ChainSpec post-processor for AuRa: upgrades <c>Genesis.Header</c> to <see cref="AuRaBlockHeader"/>
/// and stamps the step + signature parsed from <c>genesis.seal.authorityRound</c>.
/// </summary>
public static class AuRaChainSpecLoader
{
    private static readonly EthereumJsonSerializer _jsonSerializer = new();

    public static void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is null
            || chainSpec.CustomSeal?.TryGetValue("authorityRound", out JsonElement sealJson) is not true)
        {
            return;
        }

        AuRaGenesisSealJson? seal = _jsonSerializer.Deserialize<AuRaGenesisSealJson>(sealJson.GetRawText());
        if (seal?.Signature is null) return;

        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(chainSpec.Genesis.Header);
        upgraded.AuRaStep = seal.Step;
        upgraded.AuRaSignature = seal.Signature;

        chainSpec.Genesis = chainSpec.Genesis.WithReplacedHeader(upgraded);
    }

    private sealed class AuRaGenesisSealJson
    {
        public ulong Step { get; set; }
        public byte[]? Signature { get; set; }
    }
}
