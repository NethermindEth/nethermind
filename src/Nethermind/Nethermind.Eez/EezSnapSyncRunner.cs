// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using System.Threading;
using System;
using Nethermind.Synchronization.FastSync;

namespace Nethermind.Eez;

internal sealed class EezSnapSyncRunner(IStateSyncRunner inner, EezSnapBootstrap bootstrap) : IStateSyncRunner
{
    public async Task Run(CancellationToken token)
    {
        try
        {
            await inner.Run(token);
            token.ThrowIfCancellationRequested();
            bootstrap.StateSyncFinished(null);
        }
        catch (Exception error)
        {
            bootstrap.StateSyncFinished(error);
            throw;
        }
    }

    public Task RunStateSyncRounds(CancellationToken token) => inner.RunStateSyncRounds(token);
}
