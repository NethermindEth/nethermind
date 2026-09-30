// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Test;

internal sealed class NoopPbtTrieNodeCache : IPbtTrieNodeCache
{
    public static readonly NoopPbtTrieNodeCache Instance = new();

    public bool TryGet<TPath>(in ValueHash256 groupHash, TPath path, [NotNullWhen(true)] out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        payload = null;
        return false;
    }

    public void Add(PbtTransientResource transientResource) { }
}
