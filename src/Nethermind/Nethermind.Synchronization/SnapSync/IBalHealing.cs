// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public interface IBalHealing
{
    Hash256? Reassemble(IReadOnlyCollection<Hash256> updatedStorages);

    Hash256? ApplyRange(Hash256 baseRoot, BlockHeader from, BlockHeader to, CancellationToken token);

    void FinalizeSync(BlockHeader pivot);
}
