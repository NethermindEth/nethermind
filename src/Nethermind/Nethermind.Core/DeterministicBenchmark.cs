// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Benchmark-only mode, <c>NETHERMIND_DETERMINISTIC_BENCHMARK=1</c>, in which block processing does the same work on
/// every run, so per-block instruction counts can be compared between builds.
/// </summary>
/// <remarks>
/// It fixes the per-process hash seeds, keeps work that otherwise races the processing thread off the block's path,
/// does not reuse pooled state whose capacity depends on timing, and runs block processing on a thread of its own.
/// It gives up hash-flooding protection and some concurrency, so never set it on a node that serves the network.
/// Read from the environment because the hash seeds initialise before configuration is loaded.
/// </remarks>
public static class DeterministicBenchmark
{
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("NETHERMIND_DETERMINISTIC_BENCHMARK") == "1";
}
