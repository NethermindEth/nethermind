// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.ObjectPool;
using Nethermind.Blockchain;

namespace Nethermind.Consensus.Processing.BlockLevelAccessList;

internal sealed class ReadOnlyTxProcessingEnvPooledObjectPolicy(
    IReadOnlyTxProcessingEnvFactory envFactory) : IPooledObjectPolicy<IReadOnlyTxProcessorSource>
{
    public IReadOnlyTxProcessorSource Create() => envFactory.Create();
    public bool Return(IReadOnlyTxProcessorSource obj) => true;
}
