// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.DataAvailability;

/// <summary>
/// Where a node looks up the data column sidecars it holds for a block: the spec's
/// <c>retrieve_column_sidecars</c>, restricted to one (block root, column index) at a time.
/// </summary>
/// <remarks>
/// A source only answers "do I hold this column"; it does not vouch for the sidecar. The
/// availability rule re-runs the block cross-check and the KZG/inclusion verification on whatever
/// comes back, so a source fed from an unverified path (a by-root fetch, say) cannot smuggle a bad
/// sidecar past the gate.
/// </remarks>
public interface IDataColumnSource
{
    bool TryGetColumn(Hash256 blockRoot, ulong columnIndex, [NotNullWhen(true)] out DataColumnSidecar? sidecar);
}
