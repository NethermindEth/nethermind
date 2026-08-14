using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;

namespace Nethermind.JsonRpc.Data;

public class ForkConfigSummary
{
    public required ForkConfig Current { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ForkConfig? Next { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required ForkConfig? Last { get; init; }

    /// <summary>
    /// Contains every configured fork when returned by the debug configuration endpoint.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ForkConfig>? All { get; init; }

    /// <summary>
    /// Identifies the running application when returned by the debug configuration endpoint.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppVersion? AppVersion { get; init; }
}

public sealed class AppVersion
{
    /// <summary>
    /// Gets the short application code used by the client.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Gets the application name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the application version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the source revision used to build the application.
    /// </summary>
    public required string Commit { get; init; }
}

public class ForkConfig
{
    public int? ActivationTime { get; init; }
    public int? ActivationBlock { get; init; }
    public required BlobScheduleSettingsForRpc? BlobSchedule { get; init; }
    public required ulong ChainId { get; init; }
    public required byte[] ForkId { get; init; }
    public required OrderedDictionary<string, Address> Precompiles { get; init; }
    public required OrderedDictionary<string, Address> SystemContracts { get; init; }
}

public class BlobScheduleSettingsForRpc
{
    public required int BaseFeeUpdateFraction { get; init; }
    public required int Max { get; init; }
    public required int Target { get; init; }
}
