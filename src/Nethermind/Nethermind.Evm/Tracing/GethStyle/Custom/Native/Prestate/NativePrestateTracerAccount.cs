// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

[JsonConverter(typeof(NativePrestateTracerAccountConverter))]
public class NativePrestateTracerAccount
{
    public NativePrestateTracerAccount() { }

    public NativePrestateTracerAccount(UInt256? balance, UInt256 nonce, byte[]? code)
    {
        Balance = balance;
        Nonce = nonce > 0 ? nonce : null;
        Code = code is not null && code.Length > 0 ? code : null;
    }

    public UInt256? Balance { get; set; }

    public UInt256? Nonce { get; set; }

    public byte[]? Code { get; set; }

    public Dictionary<UInt256, UInt256>? Storage { get; set; }
}
