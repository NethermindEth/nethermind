// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the config object in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
/// <remarks>
/// The <c>&lt;fork&gt;Block</c> and <c>&lt;fork&gt;Time</c> properties below are facades over two
/// internal dictionaries keyed by canonical fork name (PascalCase, case-insensitive). The same
/// dictionaries are exposed via <see cref="IHasNamedForks"/> so <see cref="HardforkLabels.ExpandAll"/>
/// can fan timestamps and blocks into the per-EIP transition fields on <c>ChainParameters</c>.
/// Per-EIP overrides (<c>Eip150Block</c>, <c>Eip155Block</c>, <c>Eip158Block</c>) are NOT routed —
/// they're EIP-level fallbacks consumed inline by <c>GethGenesisLoader</c>.
/// </remarks>
public class GethGenesisConfigJson : IHasNamedForks
{
    private readonly Dictionary<string, long> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _timestamps = new(StringComparer.OrdinalIgnoreCase);

    public ulong ChainId { get; set; }

    public long? HomesteadBlock { get => GetBlock(nameof(Homestead)); set => SetBlock(nameof(Homestead), value); }
    public long? DaoForkBlock { get => GetBlock(nameof(Dao)); set => SetBlock(nameof(Dao), value); }
    public bool? DaoForkSupport { get; set; }

    // EIP-level overrides — not fork labels, kept as plain typed properties.
    public long? Eip150Block { get; set; }
    public long? Eip155Block { get; set; }
    public long? Eip158Block { get; set; }

    public long? TangerineWhistleBlock { get => GetBlock(nameof(TangerineWhistle)); set => SetBlock(nameof(TangerineWhistle), value); }
    public long? SpuriousDragonBlock { get => GetBlock(nameof(SpuriousDragon)); set => SetBlock(nameof(SpuriousDragon), value); }
    public long? ByzantiumBlock { get => GetBlock(nameof(Byzantium)); set => SetBlock(nameof(Byzantium), value); }
    public long? ConstantinopleBlock { get => GetBlock(nameof(Constantinople)); set => SetBlock(nameof(Constantinople), value); }
    public long? PetersburgBlock { get => GetBlock(nameof(ConstantinopleFix)); set => SetBlock(nameof(ConstantinopleFix), value); }
    public long? IstanbulBlock { get => GetBlock(nameof(Istanbul)); set => SetBlock(nameof(Istanbul), value); }
    public long? MuirGlacierBlock { get => GetBlock(nameof(MuirGlacier)); set => SetBlock(nameof(MuirGlacier), value); }
    public long? BerlinBlock { get => GetBlock(nameof(Berlin)); set => SetBlock(nameof(Berlin), value); }
    public long? LondonBlock { get => GetBlock(nameof(London)); set => SetBlock(nameof(London), value); }
    public long? ArrowGlacierBlock { get => GetBlock(nameof(ArrowGlacier)); set => SetBlock(nameof(ArrowGlacier), value); }
    public long? GrayGlacierBlock { get => GetBlock(nameof(GrayGlacier)); set => SetBlock(nameof(GrayGlacier), value); }
    public long? MergeNetsplitBlock { get => GetBlock(nameof(Paris)); set => SetBlock(nameof(Paris), value); }

    public ulong? ShanghaiTime { get => GetTime(nameof(Shanghai)); set => SetTime(nameof(Shanghai), value); }
    public ulong? CancunTime { get => GetTime(nameof(Cancun)); set => SetTime(nameof(Cancun), value); }
    public ulong? PragueTime { get => GetTime(nameof(Prague)); set => SetTime(nameof(Prague), value); }
    public ulong? OsakaTime { get => GetTime(nameof(Osaka)); set => SetTime(nameof(Osaka), value); }
    public ulong? AmsterdamTime { get => GetTime(nameof(Amsterdam)); set => SetTime(nameof(Amsterdam), value); }

    public ulong? Bpo1Time { get => GetTime(nameof(BPO1)); set => SetTime(nameof(BPO1), value); }
    public ulong? Bpo2Time { get => GetTime(nameof(BPO2)); set => SetTime(nameof(BPO2), value); }
    public ulong? Bpo3Time { get => GetTime(nameof(BPO3)); set => SetTime(nameof(BPO3), value); }
    public ulong? Bpo4Time { get => GetTime(nameof(BPO4)); set => SetTime(nameof(BPO4), value); }
    public ulong? Bpo5Time { get => GetTime(nameof(BPO5)); set => SetTime(nameof(BPO5), value); }

    public UInt256? TerminalTotalDifficulty { get; set; }
    public bool? TerminalTotalDifficultyPassed { get; set; }
    public Address? DepositContractAddress { get; set; }
    public Dictionary<string, GethBlobScheduleEntry>? BlobSchedule { get; set; }

    IReadOnlyDictionary<string, long>? IHasNamedForks.NamedForkBlocks => _blocks;
    IReadOnlyDictionary<string, ulong>? IHasNamedForks.NamedForkTimestamps => _timestamps;

    private long? GetBlock(string forkName) => _blocks.TryGetValue(forkName, out long v) ? v : null;
    private void SetBlock(string forkName, long? value)
    {
        if (value is null) _blocks.Remove(forkName);
        else _blocks[forkName] = value.Value;
    }

    private ulong? GetTime(string forkName) => _timestamps.TryGetValue(forkName, out ulong v) ? v : null;
    private void SetTime(string forkName, ulong? value)
    {
        if (value is null) _timestamps.Remove(forkName);
        else _timestamps[forkName] = value.Value;
    }
}
