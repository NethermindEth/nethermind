// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Transactions;

namespace Nethermind.Merge.Plugin.Data;

public class GetInclusionListResultV1
{
    public GetInclusionListResultV1(InclusionListSummaryV1 inclusionListSummary, Transaction[] inclusionListTransactions)
    {
        this.inclusionListSummary = inclusionListSummary;
        this.inclusionListTransactions = inclusionListTransactions;
    }
    public InclusionListSummaryV1 inclusionListSummary { get; set; }
    public Transaction[] inclusionListTransactions { get; set; }
}
