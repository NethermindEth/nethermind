// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

public static partial class Eip7002Constants
{
    public static readonly Address WithdrawalRequestPredeployAddress = new("0x00000961Ef480Eb55e80D19ad83579A64c007002");

    /// <summary> Keccak-256 of the canonical withdrawal request predeploy bytecode. </summary>
    public static readonly ValueHash256 CodeHash = new("0x0345a365d2f4c5975b9f1599abe0a2ee76b7a3a731bc68781bd04c84e4858f50");
}
