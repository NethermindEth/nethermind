// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs
{
    /// <summary>
    /// Registry of all Ethereum fork specifications
    /// </summary>
    public static class ForkRegistry
    {
        public static readonly Dictionary<string, IReleaseSpec> All = new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(Frontier)] = Frontier.Instance,
            [nameof(Homestead)] = Homestead.Instance,
            [nameof(Dao)] = Dao.Instance,
            [nameof(TangerineWhistle)] = TangerineWhistle.Instance,
            [nameof(SpuriousDragon)] = SpuriousDragon.Instance,
            [nameof(Byzantium)] = Byzantium.Instance,
            [nameof(Constantinople)] = Constantinople.Instance,
            [nameof(ConstantinopleFix)] = ConstantinopleFix.Instance,
            [nameof(Istanbul)] = Istanbul.Instance,
            [nameof(MuirGlacier)] = MuirGlacier.Instance,
            [nameof(Berlin)] = Berlin.Instance,
            [nameof(London)] = London.Instance,
            [nameof(ArrowGlacier)] = ArrowGlacier.Instance,
            [nameof(GrayGlacier)] = GrayGlacier.Instance,
            [nameof(Paris)] = Paris.Instance,
            [nameof(Shanghai)] = Shanghai.Instance,
            [nameof(Cancun)] = Cancun.Instance,
            [nameof(Prague)] = Prague.Instance,
            [nameof(Osaka)] = Osaka.Instance
        };
    }
}