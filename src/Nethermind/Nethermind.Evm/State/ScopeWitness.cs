// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Evm.State;

/// <summary>
/// The storage portion of an execution witness produced by a witness-tracking
/// <see cref="IWorldStateScopeProvider.IScope"/>: the state-trie and storage-trie node RLPs along the
/// paths of every account and slot touched during the scope's lifetime, the touched keys, and the
/// contract bytecode read during execution.
/// </summary>
/// <remarks>
/// Built lazily by <see cref="IWorldStateScopeProvider.IScope.Witness"/> from the keys reported via
/// <see cref="IWorldStateScopeProvider.IScope.ReportRead(Nethermind.Core.Address)"/> /
/// <see cref="IWorldStateScopeProvider.IScope.ReportRead(in Nethermind.Core.StorageCell)"/> and the code
/// read through the scope's code database. The consensus layer assembles the full execution witness
/// (adding block headers) from this — see <c>WitnessAssembler</c>.
/// </remarks>
public sealed class ScopeWitness
{
    /// <summary>Deduplicated state-trie and storage-trie node RLPs covering every touched key.</summary>
    public required IReadOnlyList<byte[]> StateNodes { get; init; }

    /// <summary>
    /// Touched keys, ordered as <c>&lt;addr&gt;&lt;slot&gt;…&lt;addr&gt;&lt;slot&gt;…</c>: each account's
    /// 20-byte address followed by its touched slots as big-endian 32-byte values.
    /// </summary>
    public required IReadOnlyList<byte[]> Keys { get; init; }

    /// <summary>Contract bytecode read during execution, keyed (and deduplicated) by code hash.</summary>
    public required IReadOnlyCollection<byte[]> Codes { get; init; }
}
