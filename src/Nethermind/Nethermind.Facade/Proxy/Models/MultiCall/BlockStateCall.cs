// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class BlockStateCalls
{
    public BlockOverride? BlockOverrides { get; set; }
    public Dictionary<Address, AccountOverride>? StateOverrides { get; set; } = new Dictionary<Address, AccountOverride>();
    public CallTransactionModel[]? Calls { get; set; } = { };
}
