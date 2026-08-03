// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the config object in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
/// <remarks>
/// The <c>&lt;Fork&gt;Block</c> and <c>&lt;Fork&gt;Time</c> properties below are facades over two
/// internal dictionaries keyed by canonical fork-class name (PascalCase, case-insensitive). The
/// same dictionaries are exposed via <see cref="IHasNamedForks"/> so
/// <see cref="HardforkLabels.ExpandAll"/> can fan timestamps and blocks into the per-EIP
/// transition fields on <c>ChainParameters</c>. By convention the property is named
/// <c>&lt;ForkClass&gt;Block</c> or <c>&lt;ForkClass&gt;Time</c> and the helper strips that suffix
/// from <see cref="CallerMemberNameAttribute"/> to derive the dict key — properties whose
/// Geth-wire name doesn't match the Nethermind fork-class name (Petersburg / Constantinople-fix,
/// merge-netsplit / Paris, Dao-fork / Dao, Bpo / BPO casing) pass an explicit override.
/// Per-EIP overrides (<c>Eip150Block</c>, <c>Eip155Block</c>, <c>Eip158Block</c>) are NOT routed —
/// they're EIP-level fallbacks consumed inline by <c>GethGenesisLoader</c>.
/// </remarks>
public class GethGenesisConfigJson : IHasNamedForks
{
    private readonly Dictionary<string, ulong> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _timestamps = new(StringComparer.OrdinalIgnoreCase);

    public ulong ChainId { get; set; }

    public ulong? HomesteadBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? DaoForkBlock { get => GetBlock(nameof(Dao)); set => SetBlock(value, nameof(Dao)); }
    public bool? DaoForkSupport { get; set; }

    // EIP-level overrides — not fork labels, kept as plain typed properties.
    public ulong? Eip150Block { get; set; }
    public ulong? Eip155Block { get; set; }
    public ulong? Eip158Block { get; set; }

    public ulong? TangerineWhistleBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? SpuriousDragonBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? ByzantiumBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? ConstantinopleBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? PetersburgBlock { get => GetBlock(nameof(ConstantinopleFix)); set => SetBlock(value, nameof(ConstantinopleFix)); }
    public ulong? IstanbulBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? MuirGlacierBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? BerlinBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? LondonBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? ArrowGlacierBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? GrayGlacierBlock { get => GetBlock(); set => SetBlock(value); }
    public ulong? MergeNetsplitBlock { get => GetBlock(nameof(Paris)); set => SetBlock(value, nameof(Paris)); }

    public ulong? ShanghaiTime { get => GetTime(); set => SetTime(value); }
    public ulong? CancunTime { get => GetTime(); set => SetTime(value); }
    public ulong? PragueTime { get => GetTime(); set => SetTime(value); }
    public ulong? OsakaTime { get => GetTime(); set => SetTime(value); }
    public ulong? AmsterdamTime { get => GetTime(); set => SetTime(value); }

    // OIC dict matches "Bpo1" (from CallerMemberName-strip) against the BPO1 fork class.
    public ulong? Bpo1Time { get => GetTime(); set => SetTime(value); }
    public ulong? Bpo2Time { get => GetTime(); set => SetTime(value); }
    public ulong? Bpo3Time { get => GetTime(); set => SetTime(value); }
    public ulong? Bpo4Time { get => GetTime(); set => SetTime(value); }
    public ulong? Bpo5Time { get => GetTime(); set => SetTime(value); }

    public UInt256? TerminalTotalDifficulty { get; set; }
    public bool? TerminalTotalDifficultyPassed { get; set; }
    public Address? DepositContractAddress { get; set; }
    public Dictionary<string, GethBlobScheduleEntry>? BlobSchedule { get; set; }

    IReadOnlyDictionary<string, ulong>? IHasNamedForks.NamedForkBlocks => _blocks;
    IReadOnlyDictionary<string, ulong>? IHasNamedForks.NamedForkTimestamps => _timestamps;

    private ulong? GetBlock([CallerMemberName] string propertyOrForkName = "")
        => _blocks.TryGetValue(StripSuffix(propertyOrForkName, "Block"), out ulong v) ? v : null;

    private void SetBlock(ulong? value, [CallerMemberName] string propertyOrForkName = "")
    {
        string forkName = StripSuffix(propertyOrForkName, "Block");
        if (value is null) _blocks.Remove(forkName);
        else _blocks[forkName] = value.Value;
    }

    private ulong? GetTime([CallerMemberName] string propertyOrForkName = "")
        => _timestamps.TryGetValue(StripSuffix(propertyOrForkName, "Time"), out ulong v) ? v : null;

    private void SetTime(ulong? value, [CallerMemberName] string propertyOrForkName = "")
    {
        string forkName = StripSuffix(propertyOrForkName, "Time");
        if (value is null) _timestamps.Remove(forkName);
        else _timestamps[forkName] = value.Value;
    }

    private static string StripSuffix(string s, string suffix) =>
        s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
}
