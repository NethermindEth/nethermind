// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.TransactionProcessing;
public readonly struct TransactionResult(string? error)
{
    public static readonly TransactionResult Ok = new();
    public static readonly TransactionResult MalformedTransaction = new("malformed");
    [MemberNotNullWhen(true, nameof(Fail))]
    [MemberNotNullWhen(false, nameof(Success))]
    public string? Error { get; } = error;
    public bool Fail => Error is not null;
    public bool Success => Error is null;
    public static implicit operator TransactionResult(string? error) => new(error);
    public static implicit operator bool(TransactionResult result) => result.Success;
}
