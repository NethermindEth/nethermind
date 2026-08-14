// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Ethereum.Test.Base;

/// <summary>
/// Pyspec transaction-test case: validates that decoding (and validating) the raw
/// <see cref="TxBytes"/> against the named <see cref="Fork"/> matches the expected
/// outcome — either successful decode + validation, or a specific
/// <see cref="ExpectedException"/> token (matching the EEST <c>TransactionException</c>
/// taxonomy, possibly with <c>|</c>-separated alternatives).
/// </summary>
public class TransactionTest : EthereumTest
{
    public string? Fork { get; set; }
    public string? TxBytes { get; set; }
    public string? ExpectedException { get; set; }
    public string? ExpectedIntrinsicGas { get; set; }
}
