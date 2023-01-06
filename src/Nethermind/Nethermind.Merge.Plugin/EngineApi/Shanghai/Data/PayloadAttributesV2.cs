// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text;
using Nethermind.Merge.Plugin.EngineApi.Paris.Data;

namespace Nethermind.Merge.Plugin.EngineApi.Shanghai.Data;

public class PayloadAttributesV2 : PayloadAttributesV1
{
    public IReadOnlyList<WithdrawalV1>? Withdrawals { get; set; } = null;

    protected override StringBuilder BuildString(StringBuilder builder, string indentation) =>
        base.BuildString(builder, indentation)
            .Remove(builder.Length - 1, 1)
            .Append($", {nameof(Withdrawals)} count: {Withdrawals?.Count}")
            .Append('}');
}
