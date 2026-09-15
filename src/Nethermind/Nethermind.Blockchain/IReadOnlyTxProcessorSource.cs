// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;

namespace Nethermind.Blockchain;

/// <summary>
/// Factory for per-block read-only transaction processing scopes.
/// Each instance owns resources tied to a specific state view and must be disposed
/// when no longer needed to release those resources promptly.
/// </summary>
public interface IReadOnlyTxProcessorSource : IDisposable
{
    /// <summary>Attempts to open the state committed at <paramref name="baseBlock"/> (pre-genesis when <c>null</c>).</summary>
    /// <returns><c>false</c> when that state is unavailable.</returns>
    bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope);

    /// <summary>Attempts to open the state required to execute <paramref name="targetBlock"/>, i.e. its parent state.</summary>
    /// <returns><c>false</c> when the parent header or its state is unavailable.</returns>
    bool TryBuildAtTarget(BlockHeader targetBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope);
}

public static class ReadOnlyTxProcessorSourceExtensions
{
    /// <inheritdoc cref="IReadOnlyTxProcessorSource.TryBuild"/>
    /// <exception cref="InvalidOperationException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IReadOnlyTxProcessingScope Build(this IReadOnlyTxProcessorSource source, BlockHeader? baseBlock) =>
        source.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? scope)
            ? scope
            : throw new InvalidOperationException($"State is unavailable for base block {baseBlock?.ToString(BlockHeader.Format.Short) ?? "pre-genesis"}.");

    /// <inheritdoc cref="IReadOnlyTxProcessorSource.TryBuildAtTarget"/>
    /// <exception cref="InvalidOperationException">The parent header or its state is unavailable.</exception>
    public static IReadOnlyTxProcessingScope BuildAtTarget(this IReadOnlyTxProcessorSource source, BlockHeader targetBlock) =>
        source.TryBuildAtTarget(targetBlock, out IReadOnlyTxProcessingScope? scope)
            ? scope
            : throw new InvalidOperationException($"Parent state is unavailable for target block {targetBlock.ToString(BlockHeader.Format.Short)}.");
}
