// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip6110Constants
{
    public static Address MainnetDepositContractAddress => field ??= new("0x00000000219ab540356cbb839cbe05303d7705fa");
    public static Address HoleskyDepositContractAddress => field ??= new("0x4242424242424242424242424242424242424242");
    public static Address SepoliaDepositContractAddress => field ??= new("0x7f02c3e3c98b133055b8b348b2ac625669ed295d");
    public static Address HoodiDepositContractAddress => field ??= new("0x00000000219ab540356cBB839Cbe05303d7705Fa");
}
