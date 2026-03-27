// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State
{
    public interface IPersistedStateInfoProvider
    {
        bool TryGetPersistedStateInfo(out PersistedStateInfo persistedStateInfo);
        bool HasRecoverableStateForBlock(BlockHeader? blockHeader);
    }

    public readonly record struct PersistedStateInfo(long BlockNumber, in ValueHash256 StateRoot);
}
