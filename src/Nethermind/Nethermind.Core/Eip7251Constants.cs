// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

public static class Eip7251Constants
{
    public const string ContractAddressKey = "CONSOLIDATION_REQUEST_PREDEPLOY_ADDRESS";

    public static readonly Address ConsolidationRequestPredeployAddress = new("0x0000BBdDc7CE488642fb579F8B00f3a590007251");

    /// <summary> Keccak-256 of the canonical consolidation request predeploy bytecode. </summary>
    public static readonly ValueHash256 CodeHash = new("0x78c6cb5202685228bbcbfb992b1c4e116c7ec5ef11e25b8e92716cfc628ddd60");
}
