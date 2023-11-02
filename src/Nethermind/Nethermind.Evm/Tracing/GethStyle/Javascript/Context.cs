// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class Context
{
    public string type { get; set; } = null!;
    public ITypedArray<byte> from { get; set; } = null!;
    public ITypedArray<byte>? to { get; set; }
    public ITypedArray<byte> input { get; set; } = null!;
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public ulong gasPrice { get; set; }
    public BigInteger value { get; set; }
    public long block { get; set; }
    public ITypedArray<byte>? output { get; set; }
    public ITypedArray<byte>? blockHash { get; set; }
    public int? txIndex { get; set; }
    public ITypedArray<byte>? txHash { get; set; }
}
