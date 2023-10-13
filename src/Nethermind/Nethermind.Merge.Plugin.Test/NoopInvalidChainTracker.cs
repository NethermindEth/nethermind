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

    public void SetChildParent(Commitment child, Commitment parent)
    {
    }

    public void OnInvalidBlock(Commitment failedBlock, Commitment? parent)
    {
    }

    public bool IsOnKnownInvalidChain(Commitment blockHash, out Commitment? lastValidHash)
    {
        lastValidHash = null;
        return false;
    }
}
