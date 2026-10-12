// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public static partial class Eip8282Constants
{
    public static Address BuilderDepositRequestPredeployAddress => field ??= new("0x0000BFF46984E3725691FA540A8C7589300D8282");

    public static Address BuilderExitRequestPredeployAddress => field ??= new("0x000064D678505AD48F8CCB093BC65613800E8282");

    public static ValueHash256 BuilderDepositCodeHash => new("0x1dd29c1e0dbc3ab670d229dbd3438003ec9015c1df9058beeb64ff301b60b98d");

    public static ValueHash256 BuilderExitCodeHash => new("0x90a0b24eb190d6c50f00f6f751dc4c2778658abf3631aceb80586c43f8bd9f2f");
}
