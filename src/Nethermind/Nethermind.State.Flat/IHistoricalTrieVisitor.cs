// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie;

namespace Nethermind.State.Flat;

public interface IHistoricalTrieVisitor
{
    bool TryRunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx>;
}

public sealed class NullHistoricalTrieVisitor : IHistoricalTrieVisitor
{
    public static readonly NullHistoricalTrieVisitor Instance = new();

    private NullHistoricalTrieVisitor()
    {
    }

    public bool TryRunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, in StateId stateId, VisitingOptions? visitingOptions, VisitingStats? diagnostics)
        where TCtx : struct, INodeContext<TCtx> => false;
}
