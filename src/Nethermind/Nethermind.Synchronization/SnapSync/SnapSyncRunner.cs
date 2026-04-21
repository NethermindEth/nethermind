// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Synchronization.ParallelSync;

namespace Nethermind.Synchronization.SnapSync;

public class SnapSyncRunner(SimpleDispatcher<SnapSyncBatch> dispatcher) : ISnapSyncRunner
{
    public Task Run(CancellationToken token) => dispatcher.Run(token);
}
