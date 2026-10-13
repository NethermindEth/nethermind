// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using NonBlocking;

namespace Nethermind.State.Flat.Rust.Interop;

/// <summary>The seam counters of the native flat DB library (<see cref="SeamCounters"/>), which have no C# counterpart.</summary>
public static class Metrics
{
    [CounterMetric]
    [Description("Calls into the native flat DB library by export, and reads of its snapshot bundles by where they were served from (bundle_*), for FlatDb.Backend=Rust and RustPersistence")]
    [KeyIsLabel("counter")]
    public static ConcurrentDictionary<string, long> FlatRustSeamCalls { get; } = new();

    [CounterMetric]
    [Description("Nanoseconds spent in the native flat DB library by export, while the NETHERMIND_FLATDB_SEAM_TIMING environment variable is 1")]
    [KeyIsLabel("counter")]
    public static ConcurrentDictionary<string, long> FlatRustSeamNanoseconds { get; } = new();
}
