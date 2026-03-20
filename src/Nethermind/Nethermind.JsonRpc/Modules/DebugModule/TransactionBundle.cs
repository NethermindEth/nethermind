// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Modules.DebugModule;

public class TransactionBundle
{
    public TransactionForRpc[] Transactions { get; set; } = [];

    public BlockOverride? BlockOverride { get; set; }

    public Dictionary<Address, AccountOverride>? StateOverrides { get; set; }
}
