// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using System;
using System.Text;

namespace Nethermind.Specs
{
    public class SpecNameParser
    {
        public static IReleaseSpec Parse(string specName)
        {
            string unambiguousSpecName = new StringBuilder(specName)
                .Replace("EIP150", "TangerineWhistle")
                .Replace("EIP158", "SpuriousDragon")
                .Replace("DAO", "Dao")
                .Replace("Merged", "Paris")
                .Replace("Merge", "Paris")
                .Replace("London+3540+3670", "Shanghai")
                .Replace("GrayGlacier+3540+3670", "Shanghai")
                .Replace("GrayGlacier+3860", "Shanghai")
                .Replace("GrayGlacier+3855", "Shanghai")
                .Replace("Merge+3540+3670", "Shanghai")
                .Replace("Shanghai+3855", "Shanghai")
                .Replace("Shanghai+3860", "Shanghai")
                .Replace("GrayGlacier+1153", "Cancun")
                .Replace("Merge+1153", "Cancun")
                .Replace("Shanghai+6780", "Cancun")
                .Replace("GrayGlacier+1153", "Cancun")
                .Replace("Merge+1153", "Cancun")
                .ToString();

            return unambiguousSpecName switch
            {
                "Frontier" => Frontier.Instance,
                "Homestead" => Homestead.Instance,
                "TangerineWhistle" => TangerineWhistle.Instance,
                "SpuriousDragon" => SpuriousDragon.Instance,
                "EIP150" => TangerineWhistle.Instance,
                "EIP158" => SpuriousDragon.Instance,
                "Dao" => Dao.Instance,
                "Constantinople" => Constantinople.Instance,
                "ConstantinopleFix" => ConstantinopleFix.Instance,
                "Byzantium" => Byzantium.Instance,
                "Istanbul" => Istanbul.Instance,
                "Berlin" => Berlin.Instance,
                "London" => London.Instance,
                "ArrowGlacier" => ArrowGlacier.Instance,
                "GrayGlacier" => GrayGlacier.Instance,
                "Shanghai" => Shanghai.Instance,
                "Cancun" => Cancun.Instance,
                "Paris" => Paris.Instance,
                "Prague" => Prague.Instance,
                "Osaka" => Osaka.Instance,
                _ => throw new NotSupportedException()
            };
        }
    }
}
