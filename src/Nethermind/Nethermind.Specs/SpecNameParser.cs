// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using System;
using System.Collections.Concurrent;
using System.Text;

namespace Nethermind.Specs
{
    public class SpecNameParser
    {
        private static readonly ConcurrentDictionary<string, IReleaseSpec> _specs = new(StringComparer.Ordinal);

        public static IReleaseSpec Parse(string specName) => _specs.GetOrAdd(specName, ParseUncached);

        private static IReleaseSpec ParseUncached(string specName)
        {
            string unambiguousSpecName = new StringBuilder(specName)
                .Replace("EIP150", "TangerineWhistle")
                .Replace("EIP158", "SpuriousDragon")
                .Replace("DAO", "Dao")
                .Replace("Merged", "Paris")
                .Replace("Merge", "Paris")
                .Replace("GrayGlacier+3860", "Shanghai")
                .Replace("GrayGlacier+3855", "Shanghai")
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
                nameof(Frontier) => Frontier.Instance,
                nameof(Homestead) => Homestead.Instance,
                nameof(TangerineWhistle) => TangerineWhistle.Instance,
                nameof(SpuriousDragon) => SpuriousDragon.Instance,
                "EIP150" => TangerineWhistle.Instance,
                "EIP158" => SpuriousDragon.Instance,
                nameof(Dao) => Dao.Instance,
                nameof(Constantinople) => Constantinople.Instance,
                nameof(ConstantinopleFix) => ConstantinopleFix.Instance,
                nameof(Byzantium) => Byzantium.Instance,
                nameof(Istanbul) => Istanbul.Instance,
                nameof(Berlin) => Berlin.Instance,
                nameof(London) => London.Instance,
                nameof(ArrowGlacier) => ArrowGlacier.Instance,
                nameof(GrayGlacier) => GrayGlacier.Instance,
                nameof(Shanghai) => Shanghai.Instance,
                nameof(Cancun) => Cancun.Instance,
                nameof(Paris) => Paris.Instance,
                nameof(Prague) => Prague.Instance,
                nameof(Osaka) => Osaka.Instance,
                nameof(BPO1) => BPO1.Instance,
                nameof(BPO2) => BPO2.Instance,
                nameof(BPO3) => BPO3.Instance,
                nameof(BPO4) => BPO4.Instance,
                nameof(BPO5) => BPO5.Instance,
                nameof(Amsterdam) => Amsterdam.Instance,
                nameof(Bogota) => Bogota.Instance,
                nameof(Eip8141Prototype) => Eip8141Prototype.Instance,
                nameof(Eip8250Prototype) => Eip8250Prototype.Instance,
                nameof(Eip8272Prototype) => Eip8272Prototype.Instance,
                nameof(Eip7906Prototype) => Eip7906Prototype.Instance,
                nameof(Eip4758Prototype) => Eip4758Prototype.Instance,
                nameof(FramesDevnet1) => FramesDevnet1.Instance,
                nameof(Eip8369Prototype) => Eip8369Prototype.Instance,
                nameof(BogotaDevnet2) => BogotaDevnet2.Instance,
                _ => throw new NotSupportedException(specName == unambiguousSpecName
                    ? $"Unknown fork name '{specName}'"
                    : $"Unknown fork name '{specName}' (resolved to '{unambiguousSpecName}')")
            };
        }
    }
}
