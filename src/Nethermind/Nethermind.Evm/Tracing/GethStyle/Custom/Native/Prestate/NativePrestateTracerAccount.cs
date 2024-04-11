// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

[JsonConverter(typeof(NativePrestateTracerAccountConverter))]
public class NativePrestateTracerAccount
{
    public NativePrestateTracerAccount(UInt256 balance)
    {
        Balance = balance;
    }

    public NativePrestateTracerAccount(UInt256 balance, ulong nonce, byte[]? code)
    {
        Balance = balance;
        Nonce = nonce > 0 ? nonce : null;
        Code = code is not null && code.Length > 0 ? code : null;
    }

    public UInt256 Balance { get; init; }

    public ulong? Nonce { get; init; }

    public byte[]? Code { get; init; }

    public Dictionary<UInt256, UInt256>? Storage { get; set; }
}
