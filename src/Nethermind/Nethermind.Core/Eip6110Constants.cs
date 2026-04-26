// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip6110Constants
{
    public const string ContractAddressKey = "DEPOSIT_CONTRACT_ADDRESS";

    public static readonly Address MainnetDepositContractAddress = new("0x00000000219ab540356cbb839cbe05303d7705fa");
    public static readonly Address HoleskyDepositContractAddress = new("0x4242424242424242424242424242424242424242");
    public static readonly Address SepoliaDepositContractAddress = new("0x7f02c3e3c98b133055b8b348b2ac625669ed295d");
    public static readonly Address HoodiDepositContractAddress = new("0x00000000219ab540356cBB839Cbe05303d7705Fa");
}
