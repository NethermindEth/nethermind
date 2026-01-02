// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.InvalidChainTracker;

namespace Nethermind.Merge.Plugin.Test;

public class NoopInvalidChainTracker : IInvalidChainTracker
{
    public void Dispose()
    {
    }

    public void SetChildParent(Hash256 child, Hash256 parent)
    {
    }

    public void OnInvalidBlock(Hash256 failedBlock, Hash256? parent)
    {
    }

    public bool IsOnKnownInvalidChain(Hash256 blockHash, out Hash256? lastValidHash)
    {
        lastValidHash = null;
        return false;
    }
}
