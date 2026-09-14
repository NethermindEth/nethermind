// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

/// <summary>
/// Factory for per-block read-only transaction processing scopes.
/// Each instance owns resources tied to a specific state view and must be disposed
/// when no longer needed to release those resources promptly.
/// </summary>
public interface IReadOnlyTxProcessorSource : IDisposable
{
    IReadOnlyTxProcessingScope Build(BlockHeader? baseBlock);

    /// <summary>Opens the state required to execute <paramref name="targetBlock"/>, i.e. its parent state.</summary>
    /// <exception cref="InvalidOperationException">The parent header or its state is unavailable.</exception>
    IReadOnlyTxProcessingScope BuildAtTarget(BlockHeader targetBlock);
}
