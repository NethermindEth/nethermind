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

    /// <summary>
    /// Removes <c>gasPrice</c>, <c>maxFeePerGas</c>, <c>maxPriorityFeePerGas</c> and
    /// <c>maxFeePerBlobGas</c> from each call object.
    /// </summary>
    /// <remarks>
    /// A capture's fee fields were priced against the base fee at capture time, so replaying them at a
    /// later head rejects any call whose fee has since fallen below the base fee. The rejected share
    /// drifts with the network, and a rejected call returns without executing, which silently flatters
    /// throughput. Stripping trades that drift for a fixed, smaller distortion: the call executes with
    /// an effective gas price of zero, so a contract that branches on GASPRICE can take a different
    /// path. BASEFEE is unaffected. Only the top-level call object is stripped; transactions nested
    /// inside an <c>eth_simulateV1</c> payload keep their captured fees.
    /// </remarks>
    public bool StripFeeFields { get; init; } = true;

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
