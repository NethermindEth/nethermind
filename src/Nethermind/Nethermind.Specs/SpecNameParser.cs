// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using System;

namespace Nethermind.Specs
{
    public class SpecNameParser
    {
        public static IReleaseSpec Parse(string specName)
        {
            specName = specName.Replace("EIP150", "TangerineWhistle");
            specName = specName.Replace("EIP158", "SpuriousDragon");
            specName = specName.Replace("DAO", "Dao");
            specName = specName.Replace("Merged", "Paris");
            specName = specName.Replace("Merge", "Paris");
            specName = specName.Replace("London+3540+3670", "Shanghai");
            specName = specName.Replace("GrayGlacier+3540+3670", "Shanghai");
            specName = specName.Replace("GrayGlacier+3860", "Shanghai");
            specName = specName.Replace("GrayGlacier+3855", "Shanghai");
            specName = specName.Replace("Merge+3540+3670", "Shanghai");
            specName = specName.Replace("Shanghai+3855", "Shanghai");
            specName = specName.Replace("Shanghai+3860", "Shanghai");
            specName = specName.Replace("GrayGlacier+1153", "Cancun");
            specName = specName.Replace("Merge+1153", "Cancun");
            specName = specName.Replace("Shanghai+6780", "Cancun");
            specName = specName.Replace("GrayGlacier+1153", "Cancun");
            specName = specName.Replace("Merge+1153", "Cancun");
            return specName switch
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
