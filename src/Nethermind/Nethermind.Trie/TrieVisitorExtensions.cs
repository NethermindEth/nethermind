// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie;

public static class TrieVisitorExtensions
{
    public static void Traverse<TNodeContext>(
        this ITreeVisitor<TNodeContext> visitor,
        Hash256 rootHash,
        ITrieStore trieStore,
        VisitingOptions? visitingOptions = null
    ) where TNodeContext : struct, INodeContext<TNodeContext> =>
        TraverseCore(visitor, rootHash, trieStore.GetTrieStore(null), visitingOptions, isStorage: false, trieStore);

    public static void TraverseState<TNodeContext>(
        this ITreeVisitor<TNodeContext> visitor,
        Hash256 rootHash,
        ITrieNodeResolver resolver,
        VisitingOptions? visitingOptions = null
    ) where TNodeContext : struct, INodeContext<TNodeContext> =>
        TraverseCore(visitor, rootHash, resolver, visitingOptions, isStorage: false, trieStore: null);

    public static void TraverseStorage<TNodeContext>(
        this ITreeVisitor<TNodeContext> visitor,
        Hash256 rootHash,
        ITrieNodeResolver resolver,
        VisitingOptions? visitingOptions = null
    ) where TNodeContext : struct, INodeContext<TNodeContext> =>
        TraverseCore(visitor, rootHash, resolver, visitingOptions, isStorage: true, trieStore: null);

    private static void TraverseCore<TNodeContext>(
        ITreeVisitor<TNodeContext> visitor,
        Hash256 rootHash,
        ITrieNodeResolver resolver,
        VisitingOptions? visitingOptions,
        bool isStorage,
        ITrieStore? trieStore
    ) where TNodeContext : struct, INodeContext<TNodeContext>
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ArgumentNullException.ThrowIfNull(rootHash);
        visitingOptions ??= VisitingOptions.Default;

        using TrieVisitContext trieVisitContext = new()
        {
            MaxDegreeOfParallelism = visitingOptions.MaxDegreeOfParallelism,
            IsStorage = isStorage
        };

        ReadFlags flags = visitor.ExtraReadFlag;
        if (visitor.IsFullDbScan)
        {
            if (resolver.Scheme == INodeStorage.KeyScheme.HalfPath)
            {
                // With halfpath or flat, the nodes are ordered so readahead will make things faster.
                flags |= ReadFlags.HintReadAhead;
            }
            else
            {
                // With hash, we don't wanna add cache as that will take some CPU time away.
                flags |= ReadFlags.HintCacheMiss;
            }
        }

        ITrieNodeResolver effectiveResolver = flags != ReadFlags.None
            ? new TrieNodeResolverWithReadFlags(resolver, flags)
            : resolver;

        bool TryGetRootRef(out TrieNode? rootRef)
        {
            rootRef = null;
            if (rootHash != Keccak.EmptyTreeHash)
            {
                TreePath emptyPath = TreePath.Empty;
                rootRef = effectiveResolver.FindCachedOrUnknown(emptyPath, rootHash);
                if (!rootRef!.TryResolveNode(effectiveResolver, ref emptyPath))
                {
                    visitor.VisitMissingNode(default, rootHash);
                    return false;
                }
            }

            return true;
        }

        if (!visitor.IsFullDbScan)
        {
            visitor.VisitTree(default, rootHash);
            if (TryGetRootRef(out TrieNode? rootRef) && rootRef is not null)
            {
                TreePath emptyPath = TreePath.Empty;
                RecursiveTrieVisitor<TNodeContext> traverser = new(visitor, trieVisitContext, trieStore);
                traverser.Start(rootRef, default, effectiveResolver, ref emptyPath);
            }
        }
        // Full db scan
        else if (resolver.Scheme == INodeStorage.KeyScheme.Hash && visitingOptions.FullScanMemoryBudget != 0)
        {
            visitor.VisitTree(default, rootHash);
            BatchedTrieVisitor<TNodeContext> batchedTrieVisitor = new(visitor, effectiveResolver, visitingOptions);
            batchedTrieVisitor.Start(rootHash, trieVisitContext);
        }
        else if (TryGetRootRef(out TrieNode? rootRef) && rootRef is not null)
        {
            TreePath emptyPath = TreePath.Empty;
            visitor.VisitTree(default, rootHash);
            RecursiveTrieVisitor<TNodeContext> traverser = new(visitor, trieVisitContext, trieStore);
            traverser.Start(rootRef, default, effectiveResolver, ref emptyPath);
        }
    }
}
