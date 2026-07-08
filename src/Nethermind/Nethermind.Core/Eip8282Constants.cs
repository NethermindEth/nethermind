// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// EIP-8282 builder execution request predeploys, system-called at the end of each block
/// to dequeue builder deposit and builder exit requests.
/// </summary>
public static class Eip8282Constants
{
    public static readonly Address BuilderDepositRequestPredeployAddress = new("0x0000884d2AA32eAa155F59A2f24eFa73D9008282");

    public static readonly Address BuilderExitRequestPredeployAddress = new("0x000014574A74c805590AFF9499fc7A690f008282");
}
