// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Facade.Proxy.Models.MultiCall;

public class TransactionWithSourceDetails
{
    public bool HadNonceInRequest;
    public bool HadGasLimitInRequest;
    public Transaction Transaction { get; set; }
}
