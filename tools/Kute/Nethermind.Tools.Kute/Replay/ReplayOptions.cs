// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.Replay;

/// <summary>Settings for one replay run.</summary>
public sealed record ReplayOptions
{
    /// <summary>Trace file to replay; <c>.jsonl</c>, <c>.jsonl.gz</c> or <c>.jsonl.zst</c>.</summary>
    public required string InputPath { get; init; }

    /// <summary>Endpoint receiving the requests.</summary>
    public required Uri Address { get; init; }

    /// <summary>Concurrency levels to measure, in the order they are run.</summary>
    public required IReadOnlyList<int> Concurrencies { get; init; }

    /// <summary>
    /// Block parameter to force on every request, or <see langword="null"/> to replay the captured one.
    /// </summary>
    public string? BlockTag { get; init; } = "latest";

    /// <summary>Requests measured per level; <c>0</c> replays the whole trace.</summary>
    public int MeasuredRequests { get; init; } = 2000;

    /// <summary>Requests sent and discarded before each measured window.</summary>
    public int WarmupRequests { get; init; } = 200;

    /// <summary>Records skipped at the start of the trace.</summary>
    public int Skip { get; init; }

    /// <summary>Stops a level early once its measured window reaches this duration; <c>null</c> for no cap.</summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>Per-request HTTP timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Streams and rewrites the trace without sending anything, to validate it.</summary>
    public bool DryRun { get; init; }

    /// <summary>Writes per-level progress to standard error.</summary>
    public bool Progress { get; init; }

    /// <summary>Path to a hex-encoded JWT secret, for endpoints that require authentication.</summary>
    public string? SecretPath { get; init; }
}
