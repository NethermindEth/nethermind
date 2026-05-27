// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public interface IShareableTxProcessorSource : IDisposable
{
    /// <summary>
    /// Attempt to build a processing scope anchored at <paramref name="baseBlock"/>. Returns <c>false</c>
    /// when the state for that block is no longer available (e.g. pruned concurrently).
    /// </summary>
    bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope);
}
