// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;

namespace Nethermind.Blockchain.BeaconBlockRoot;

// Lazy, so the guest runs no class constructor for this type.
public partial class BeaconBlockRootHandler
{
    private static AccessList BeaconRootsOnlyAccessList => field ??= BuildAddressOnlyAccessList(Eip4788Constants.BeaconRootsAddress);
}
