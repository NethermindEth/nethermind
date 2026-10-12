// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Eip2930;

namespace Nethermind.Blockchain.BeaconBlockRoot;

public partial class BeaconBlockRootHandler
{
    private static readonly AccessList BeaconRootsOnlyAccessList = BuildAddressOnlyAccessList(Eip4788Constants.BeaconRootsAddress);
}
