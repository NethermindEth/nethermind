// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class MultiCallBlockStateCallsModel
{
    public BlockOverride BlockOverride { get; set; }
    public AccountOverride[] StateOverrides { get; set; }
    public CallTransactionModel[] Calls { get; set; }
}
