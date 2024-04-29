// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.Native.Prestate;

[JsonConverter(typeof(NativePrestateTracerAccountConverter))]
public class NativePrestateTracerAccount
{
    public NativePrestateTracerAccount(UInt256 balance, bool isPrestate = true)
    {
        Balance = balance;
        IsPrestate = isPrestate;
    }

    public NativePrestateTracerAccount(UInt256 balance, UInt256 nonce, byte[]? code, bool isPrestate = true)
    {
        Balance = balance;
        Nonce = nonce > 0 ? nonce : null;
        Code = code is not null && code.Length > 0 ? code : null;
        IsPrestate = isPrestate;
    }

    public UInt256 Balance { get; }

    public UInt256? Nonce { get; }

    public byte[]? Code { get; }

    public Dictionary<UInt256, UInt256>? Storage { get; set; }

    [JsonIgnore]
    public bool IsPrestate { get; set; }
}
