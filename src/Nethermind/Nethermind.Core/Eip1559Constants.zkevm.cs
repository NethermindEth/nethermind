// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core;

// Computed on access, so the guest runs no class constructor for this type.
public partial class Eip1559Constants
{
    public static UInt256 DefaultForkBaseFee => 1.GWei;
}
