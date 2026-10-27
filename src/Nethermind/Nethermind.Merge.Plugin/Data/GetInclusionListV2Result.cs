// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>The <c>InclusionListV1</c> that <c>engine_getInclusionListV2</c> returns: the list's transactions and its EIP-8288
/// <c>recursiveStark</c>.</summary>
/// <remarks>Owns <see cref="Transactions"/>, which the JSON-RPC response disposes after serialising.</remarks>
public sealed class GetInclusionListV2Result(InclusionListBytes transactions, RecursiveStarkV1 recursiveStark) : IDisposable
{
    public InclusionListBytes Transactions { get; } = transactions;

    public RecursiveStarkV1 RecursiveStark { get; } = recursiveStark;

    public void Dispose() => Transactions.Dispose();
}
