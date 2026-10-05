// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Nethermind.BeaconChain.Test.Api;

public abstract class BeaconApiFixture
{
    private protected BeaconApiTestHost _host = null!;

    [OneTimeTearDown]
    public async Task DisposeHost() => await _host.DisposeAsync();
}
