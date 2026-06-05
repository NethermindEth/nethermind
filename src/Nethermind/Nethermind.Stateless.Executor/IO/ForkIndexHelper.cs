// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Stateless.Execution.IO;

internal static class ForkIndexHelper
{
    private static readonly Dictionary<string, ulong> _forkIndexes = new(StringComparer.Ordinal)
    {
        [Frontier.Instance.Name] = 0,
        [Homestead.Instance.Name] = 1,
        [Dao.Instance.Name] = 2,
        [TangerineWhistle.Instance.Name] = 3,
        [SpuriousDragon.Instance.Name] = 4,
        [Byzantium.Instance.Name] = 5,
        [ConstantinopleFix.Instance.Name] = 7, // TODO: Decrease by 1 for removed Constantinople on the next fixture release
        [Istanbul.Instance.Name] = 8,
        [MuirGlacier.Instance.Name] = 9,
        [Berlin.Instance.Name] = 10,
        [London.Instance.Name] = 11,
        [ArrowGlacier.Instance.Name] = 12,
        [GrayGlacier.Instance.Name] = 13,
        [Paris.Instance.Name] = 14,
        [Shanghai.Instance.Name] = 15,
        [Cancun.Instance.Name] = 16,
        [Prague.Instance.Name] = 17,
        [Osaka.Instance.Name] = 18,
        [BPO1.Instance.Name] = 19,
        [BPO2.Instance.Name] = 20,
        [Amsterdam.Instance.Name] = 24  // TODO: Decrease by 3 for removed BPOs on the next fixture release
    };

    internal static ulong GetForkIndexByName(string name) =>
        _forkIndexes.TryGetValue(name, out ulong index) ? index : throw new ArgumentException($"Unknown fork: {name}", nameof(name));

    internal static string? GetForkNameByIndex(ulong index) => index switch
    {
        0 => Frontier.Instance.Name,
        1 => Homestead.Instance.Name,
        2 => Dao.Instance.Name,
        3 => TangerineWhistle.Instance.Name,
        4 => SpuriousDragon.Instance.Name,
        5 => Byzantium.Instance.Name,
        7 => ConstantinopleFix.Instance.Name, // TODO: Decrease by 1 for removed Constantinople on the next fixture release
        8 => Istanbul.Instance.Name,
        9 => MuirGlacier.Instance.Name,
        10 => Berlin.Instance.Name,
        11 => London.Instance.Name,
        12 => ArrowGlacier.Instance.Name,
        13 => GrayGlacier.Instance.Name,
        14 => Paris.Instance.Name,
        15 => Shanghai.Instance.Name,
        16 => Cancun.Instance.Name,
        17 => Prague.Instance.Name,
        18 => Osaka.Instance.Name,
        19 => BPO1.Instance.Name,
        20 => BPO2.Instance.Name,
        24 => Amsterdam.Instance.Name,  // TODO: Decrease by 3 for removed BPOs on the next fixture release
        _ => null
    };
}
