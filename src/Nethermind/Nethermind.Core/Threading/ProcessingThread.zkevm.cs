// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Threading;

/// <summary>
/// Constant-false flag for the zkVM guest — see the std counterpart for the real one.
/// </summary>
/// <remarks>
/// The guest never runs <c>BlockchainProcessor</c>, so the std flag is already false throughout.
/// Folding it to a constant lets the metrics counters that branch on it compile away entirely,
/// and keeps the callers that gate work on it (e.g. <c>SetAccountChanges</c>) behaving as before.
/// </remarks>
public static partial class ProcessingThread
{
    public static bool IsBlockProcessingThread { get => false; set { } }
}
