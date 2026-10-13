// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Eez.Execution;

/// <summary>
/// The EIP-4788 handler of a chain whose blocks carry a zero parent beacon root and never call the beacon roots
/// contract, even if code is later deployed at its address.
/// </summary>
public sealed class NoBeaconRootsCall : IBeaconBlockRootHandler
{
    public static NoBeaconRootsCall Instance { get; } = new();

    public (Address? toAddress, AccessList? accessList) BeaconRootsAccessList(Block block, IReleaseSpec spec, bool includeStorageCells = true) => (null, null);

    public void StoreBeaconRoot(Block block, IReleaseSpec spec, ITxTracer tracer)
    {
    }

    public AccessList? GetAccessList(Block block, IReleaseSpec spec) => null;
}
