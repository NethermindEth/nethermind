using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.JsonRpc.Data;

public class EthConfig
{
    public ForkConfig Current { get; init; }
    public string CurrentHash { get; init; }

    public ForkConfig? Next { get; init; }
    public string? NextHash { get; init; }
}

public class ForkConfig
{
    public ulong ActivationTime { get; init; }
    public BlobScheduleSettings? BlobSchedule { get; init; }
    public ulong ChainId { get; init; }
    public Dictionary<Address, string> Precompiles { get; init; }
    public Dictionary<Address, string> SystemContracts { get; init; }
}
