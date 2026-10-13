// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class TransactionWithSourceDetails
{
    public bool HadGasLimitInRequest;
    public bool HadNonceInRequest;

    /// <summary>The gas limit the block must still be able to include, or <c>null</c> to skip that check.</summary>
    public ulong? GasLimitToFitBlock;

    /// <summary>Whether the call fails at the maximum sender nonce even without validation (EIP-2681).</summary>
    public bool EnforceNonceLimit = true;
    public Transaction Transaction { get; set; }
}
