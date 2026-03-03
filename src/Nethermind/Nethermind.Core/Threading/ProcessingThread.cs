// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

/// <summary>
/// Identifies the current thread as the main block processing thread.
/// Set by BlockchainProcessor before processing blocks.
/// When true, metric counters use plain static increments (single writer, no sync).
/// When false, metric counters use Interlocked on separate aggregate fields.
/// </summary>
public static class ProcessingThread
{
    [ThreadStatic]
    public static bool IsBlockProcessingThread;
}
