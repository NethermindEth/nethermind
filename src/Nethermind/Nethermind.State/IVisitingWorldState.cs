// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Trie;

namespace Nethermind.State;

public interface IVisitingWorldState : IWorldState
{
    /// <summary>
    /// Runs a visitor over trie.
    /// </summary>
    /// <param name="visitor">Visitor to run.</param>
    /// <param name="stateRoot">Root to run on.</param>
    /// <param name="visitingOptions">Options to run visitor.</param>
    void Accept<TCtx>(ITreeVisitor<TCtx> visitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null)
        where TCtx : struct, INodeContext<TCtx>;
}
