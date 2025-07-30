using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Data;

public class EthConfig
{
    public required ForkConfig Current { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ForkConfig? Next { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ForkConfig? Last { get; init; }
}

public class ForkConfig
{
    public int? ActivationTime { get; init; }
    public int? ActivationBlock { get; init; }
    public required BlobScheduleSettingsForRpc? BlobSchedule { get; init; }
    public required ulong ChainId { get; init; }
    public byte[]? ForkId { get; init; }
    public required OrderedDictionary<string, Address> Precompiles { get; init; }
    public required OrderedDictionary<string, Address> SystemContracts { get; init; }
}

public class BlobScheduleSettingsForRpc
{
    public required int BaseFeeUpdateFraction { get; init; }
    public required int Max { get; init; }
    public required int Target { get; init; }
}
