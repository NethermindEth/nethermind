// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// ChainSpec post-processor for AuRa: upgrades <c>Genesis.Header</c> to <see cref="AuRaBlockHeader"/>
/// and stamps the step + signature parsed from <c>genesis.seal.authorityRound</c>.
/// </summary>
public static class AuRaChainSpecLoader
{
    public static void ProcessChainSpec(ChainSpec chainSpec)
    {
        if (chainSpec.Genesis is null || chainSpec.GenesisAuRaSeal is null) return;

        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(chainSpec.Genesis.Header);
        upgraded.AuRaStep = chainSpec.GenesisAuRaSeal.Step;
        upgraded.AuRaSignature = chainSpec.GenesisAuRaSeal.Signature;

        chainSpec.Genesis = chainSpec.Genesis.WithReplacedHeader(upgraded);
    }
}
