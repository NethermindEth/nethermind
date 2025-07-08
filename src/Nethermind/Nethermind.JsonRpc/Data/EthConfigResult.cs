using System.Collections.Generic;
using System.Text.Json.Nodes;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Data;

public class EthConfig
{
    public JsonNode Current { get; init; }
    public ulong CurrentHash { get; init; }
    public byte[] CurrentForkId { get; init; }
    public JsonNode? Next { get; init; }
    public ulong? NextHash { get; init; }
    public byte[]? NextForkId { get; init; }
    public ForkIdForRpc[] Forks { get; init; }
}

public class ForkConfig
{
    public int ActivationTime { get; init; }
    public BlobScheduleSettingsForRpc? BlobSchedule { get; init; }
    public ulong ChainId { get; init; }
    public Dictionary<Address, string> Precompiles { get; init; }

    public Dictionary<string, string> SystemContracts { get; init; }
}

public class ForkIdForRpc
{
    public required byte[] ForkHash { get; init; }
    public required int Next { get; init; }
}

public class BlobScheduleSettingsForRpc
{
    public int BaseFeeUpdateFraction { get; set; }
    public int Max { get; set; }
    public int Target { get; set; }
}
