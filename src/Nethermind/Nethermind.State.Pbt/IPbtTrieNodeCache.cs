// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>Shared retention of immutable node groups keyed by canonical path and logical subtree hash.</summary>
public interface IPbtTrieNodeCache
{
    /// <summary>Leases the retained group at <paramref name="path"/> whose subtree hash is <paramref name="groupHash"/>.</summary>
    /// <returns><c>true</c> when <paramref name="payload"/> holds a caller-owned lease to release with <see cref="IDisposable.Dispose"/>.</returns>
    bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>;

    /// <summary>Folds a retired block's staged groups into the shared cache once its last reader has left.</summary>
    void Add(PbtTransientResource transientResource);

    sealed class Noop : IPbtTrieNodeCache
    {
        public static readonly Noop Instance = new();

        public bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
        {
            payload = null;
            return false;
        }

        public void Add(PbtTransientResource transientResource) { }
    }
}
