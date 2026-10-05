// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.P2P;

namespace Nethermind.BeaconChain.Test.P2P;

internal sealed class PeerHostScope(params BeaconP2P[] hosts) : IAsyncDisposable
{
    public async Task StartAsync(CancellationToken token, params BeaconP2P[] startupOrder)
    {
        foreach (BeaconP2P host in startupOrder)
        {
            await host.StartAsync(token);
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(hosts.Length - 1);

    private async ValueTask DisposeAsync(int index)
    {
        if (index < 0) return;
        try
        {
            await hosts[index].DisposeAsync();
        }
        finally
        {
            await DisposeAsync(index - 1);
        }
    }
}
