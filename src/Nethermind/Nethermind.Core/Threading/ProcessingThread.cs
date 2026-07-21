// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Threading;

/// <summary>
/// Identifies the current thread as the main block processing path.
/// Set by BlockchainProcessor before processing blocks.
/// </summary>
public static class ProcessingThread
{
    [ThreadStatic]
    private static bool _isBlockProcessingThread;

    public static bool IsBlockProcessingThread
    {
        get => _isBlockProcessingThread;
        set => _isBlockProcessingThread = value;
    }
}
