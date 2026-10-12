// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip4788Constants
{
    public static Address BeaconRootsAddress => field ??= new("0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02");
}
