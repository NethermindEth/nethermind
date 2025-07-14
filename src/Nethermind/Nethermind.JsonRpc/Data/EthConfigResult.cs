using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Data;

public class EthConfig
{
    public required JsonNode Current { get; init; }
    public required ulong CurrentHash { get; init; }
    public required byte[] CurrentForkId { get; init; }

    public required JsonNode? Next { get; init; }
    public required ulong? NextHash { get; init; }
    public required byte[]? NextForkId { get; init; }

    public required JsonNode? Last { get; init; }
    public required ulong? LastHash { get; init; }
    public required byte[]? LastForkId { get; init; }
}

public class ForkConfig
{
    public int? ActivationTime { get; init; }
    public int? ActivationBlock { get; init; }
    public required BlobScheduleSettingsForRpc? BlobSchedule { get; init; }
    public required ulong ChainId { get; init; }
    public required Dictionary<Address, string> Precompiles { get; init; }
    public required Dictionary<string, Address> SystemContracts { get; init; }
}

public class BlobScheduleSettingsForRpc
{
    public required int BaseFeeUpdateFraction { get; init; }
    public required int Max { get; init; }
    public required int Target { get; init; }
}
