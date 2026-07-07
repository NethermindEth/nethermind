// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.SnapSync;

public interface IBalHealing
{
    Task<bool> Run(BlockHeader firstPivot, BlockHeader lastPivot, IReadOnlyCollection<Hash256> updatedStorageAccounts, CancellationToken token);
}
