// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// EIP-8282 builder execution request predeploys, system-called at the end of each block
/// to dequeue builder deposit and builder exit requests.
/// </summary>
public static class Eip8282Constants
{
    public const string BuilderDepositContractAddressKey = "BUILDER_DEPOSIT_CONTRACT_ADDRESS";
    public const string BuilderExitContractAddressKey = "BUILDER_EXIT_CONTRACT_ADDRESS";

    public static readonly Address BuilderDepositRequestPredeployAddress = new("0x0000BFF46984E3725691FA540A8C7589300D8282");

    public static readonly Address BuilderExitRequestPredeployAddress = new("0x000064D678505AD48F8CCB093BC65613800E8282");

    /// <summary> Keccak-256 of the canonical builder deposit request predeploy bytecode. </summary>
    public static readonly ValueHash256 BuilderDepositCodeHash = new("0x1dd29c1e0dbc3ab670d229dbd3438003ec9015c1df9058beeb64ff301b60b98d");

    /// <summary> Keccak-256 of the canonical builder exit request predeploy bytecode. </summary>
    public static readonly ValueHash256 BuilderExitCodeHash = new("0x90a0b24eb190d6c50f00f6f751dc4c2778658abf3631aceb80586c43f8bd9f2f");
}
