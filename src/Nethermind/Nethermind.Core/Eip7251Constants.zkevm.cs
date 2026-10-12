// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip7251Constants
{
    public static Address ConsolidationRequestPredeployAddress => field ??= new("0x0000BBdDc7CE488642fb579F8B00f3a590007251");

    public static ValueHash256 CodeHash => new("0x78c6cb5202685228bbcbfb992b1c4e116c7ec5ef11e25b8e92716cfc628ddd60");
}
