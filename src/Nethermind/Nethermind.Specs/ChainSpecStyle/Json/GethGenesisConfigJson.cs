// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle.Json;

/// <summary>
/// Represents the config object in a Geth-style genesis.json file as defined in EIP-7949.
/// </summary>
public class GethGenesisConfigJson
{
    public ulong ChainId { get; set; }

    public long? HomesteadBlock { get; set; }

    public long? DaoForkBlock { get; set; }

    public bool? DaoForkSupport { get; set; }

    public long? Eip150Block { get; set; }

    public long? Eip155Block { get; set; }

    public long? Eip158Block { get; set; }

    public long? TangerineWhistleBlock { get; set; }

    public long? SpuriousDragonBlock { get; set; }

    public long? ByzantiumBlock { get; set; }

    public long? ConstantinopleBlock { get; set; }

    public long? PetersburgBlock { get; set; }

    public long? IstanbulBlock { get; set; }

    public long? MuirGlacierBlock { get; set; }

    public long? BerlinBlock { get; set; }

    public long? LondonBlock { get; set; }

    public long? ArrowGlacierBlock { get; set; }

    public long? GrayGlacierBlock { get; set; }

    public long? MergeNetsplitBlock { get; set; }

    public ulong? ShanghaiTime { get; set; }

    public ulong? CancunTime { get; set; }

    public ulong? PragueTime { get; set; }

    public ulong? OsakaTime { get; set; }

    public ulong? AmsterdamTime { get; set; }

    public ulong? Eip7692Time { get; set; }

    public ulong? Eip7907Time { get; set; }

    public ulong? Bpo1Time { get; set; }

    public ulong? Bpo2Time { get; set; }

    public ulong? Bpo3Time { get; set; }

    public ulong? Bpo4Time { get; set; }

    public ulong? Bpo5Time { get; set; }

    public UInt256? TerminalTotalDifficulty { get; set; }

    public bool? TerminalTotalDifficultyPassed { get; set; }

    public Address? DepositContractAddress { get; set; }

    public Dictionary<string, GethBlobScheduleEntry> BlobSchedule { get; set; }
}

public class GethBlobScheduleEntry
{
    public ulong Target { get; set; }

    public ulong Max { get; set; }

    public ulong BaseFeeUpdateFraction { get; set; }
}
