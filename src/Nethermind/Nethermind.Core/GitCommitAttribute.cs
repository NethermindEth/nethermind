using System;

namespace Nethermind.Core;

[AttributeUsage(AttributeTargets.Assembly)]
public class GitCommitAttribute : Attribute
{
    public GitCommitAttribute(string hash, string timestamp)
    {
        Hash = hash ?? throw new ArgumentNullException(nameof(hash));

        ArgumentNullException.ThrowIfNull(timestamp);

        Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp));
    }

    public string Hash { get; }

    public DateTimeOffset Timestamp { get; }
}
