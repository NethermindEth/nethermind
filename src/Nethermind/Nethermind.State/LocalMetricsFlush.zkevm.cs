// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.State;

namespace Nethermind.State;

#pragma warning disable NETH003 // File name does not match the contained type

/// <summary>
/// No-op flush for the zkVM guest, which reads no metrics — see the std counterpart for the fold.
/// </summary>
internal static class LocalMetricsFlush
{
    // Empty body: inlined away at the call sites, so the guest skips the global fold entirely.
    internal static void Flush(this LocalMetrics m) { }
}
