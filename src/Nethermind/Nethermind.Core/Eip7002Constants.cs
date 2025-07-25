// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7002Constants
{
    public const string ContractAddressKey = "WITHDRAWAL_REQUEST_PREDEPLOY_ADDRESS";

    public static readonly Address WithdrawalRequestPredeployAddress = new("0x00000961Ef480Eb55e80D19ad83579A64c007002");
}
