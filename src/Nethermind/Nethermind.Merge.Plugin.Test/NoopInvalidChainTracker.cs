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

    public void SetChildParent(Keccak child, Keccak parent)
    {
    }

    public void OnInvalidBlock(Keccak failedBlock, Keccak? parent)
    {
    }

    public bool IsOnKnownInvalidChain(Keccak blockHash, out Keccak? lastValidHash)
    {
        lastValidHash = null;
        return false;
    }
}
