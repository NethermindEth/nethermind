// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// EIP-8282 builder execution request predeploys, system-called at the end of each block
/// to dequeue builder deposit and builder exit requests.
/// </summary>
public static partial class Eip8282Constants
{
    public const string BuilderDepositContractAddressKey = "BUILDER_DEPOSIT_CONTRACT_ADDRESS";
    public const string BuilderExitContractAddressKey = "BUILDER_EXIT_CONTRACT_ADDRESS";
}
