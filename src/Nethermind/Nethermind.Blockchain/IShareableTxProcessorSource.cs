// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Exceptions;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public interface IShareableTxProcessorSource : IDisposable
{
    /// <inheritdoc cref="IReadOnlyTxProcessorSource.TryBuild"/>
    bool TryBuild(BlockHeader? baseBlock, [NotNullWhen(true)] out IReadOnlyTxProcessingScope? scope);
}

public static class ShareableTxProcessorSourceExtensions
{
    /// <inheritdoc cref="IShareableTxProcessorSource.TryBuild"/>
    /// <exception cref="InvalidOperationException">The state at <paramref name="baseBlock"/> is unavailable.</exception>
    public static IReadOnlyTxProcessingScope Build(this IShareableTxProcessorSource source, BlockHeader? baseBlock) =>
        source.TryBuild(baseBlock, out IReadOnlyTxProcessingScope? scope)
            ? scope
            : throw new StateUnavailableException($"State is unavailable for base block {baseBlock?.ToString(BlockHeader.Format.Short) ?? "pre-genesis"}.");
}
