// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Identifies the current async flow as the main block processing path.
/// Set by BlockchainProcessor before processing blocks.
/// </summary>
public static class ProcessingThread
{
    private static readonly AsyncLocal<bool> _isBlockProcessingThread = new();

    public static bool IsBlockProcessingThread
    {
        get => _isBlockProcessingThread.Value;
        set => _isBlockProcessingThread.Value = value;
    }
}
