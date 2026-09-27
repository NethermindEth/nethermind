// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.Tracing.GethStyle;

/// <summary>The block-wide index the next log that takes effect receives; callTracer log indexes continue from it.</summary>
/// <remarks>A replay that traces every transaction of a block shares one instance, so each transaction continues
/// from the logs the preceding ones committed, whether or not the body was ever stored.</remarks>
public sealed class BlockLogIndex(Func<int>? start = null)
{
    private int? _next;

    public int Next
    {
        get => _next ??= start?.Invoke() ?? 0;
        set => _next = value;
    }
}
