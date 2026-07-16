// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// EIP-8282 builder execution request predeploys, system-called at the end of each block
/// to dequeue builder deposit and builder exit requests.
/// </summary>
public static class Eip8282Constants
{
    public static readonly Address BuilderDepositRequestPredeployAddress = new("0x0000bFF46984e3725691FA540a8C7589300D8282");

    public static readonly Address BuilderExitRequestPredeployAddress = new("0x000064D678505ad48F8cCb093BC65613800E8282");
}
