// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Ethereum.Test.Base;

public class TransactionTestJson
{
    public string? TxBytes { get; set; }
    public Dictionary<string, TransactionTestResultJson>? Result { get; set; }
}

public class TransactionTestResultJson
{
    public string? IntrinsicGas { get; set; }
    public string? Exception { get; set; }
}
