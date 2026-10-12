// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip7002Constants
{
    public static Address WithdrawalRequestPredeployAddress => field ??= new("0x00000961Ef480Eb55e80D19ad83579A64c007002");

    public static ValueHash256 CodeHash => new("0x0345a365d2f4c5975b9f1599abe0a2ee76b7a3a731bc68781bd04c84e4858f50");
}
