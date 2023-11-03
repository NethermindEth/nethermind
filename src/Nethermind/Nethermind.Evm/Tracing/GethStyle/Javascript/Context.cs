// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class Context
{
    private ITypedArray<byte>? _from;
    private ITypedArray<byte>? _to;
    private ITypedArray<byte>? _input;
    private ITypedArray<byte>? _output;
    private ITypedArray<byte>? _blockHash;
    private ITypedArray<byte>? _txHash;

    public Address? From { get; set; }
    public Address? To { get; set; }
    public ReadOnlyMemory<byte> Input { get; set; }
    public byte[]? Output { get; set; }
    public Hash256? BlockHash { get; set; }
    public Hash256? TxHash { get; set; }

    public string type { get; set; } = null!;
    public ITypedArray<byte> from => _from ??= From!.Bytes.ToScriptArray();
    public ITypedArray<byte>? to => _to ??= To!.Bytes.ToScriptArray();
    public ITypedArray<byte> input => _input ??= Input.ToArray().ToScriptArray();
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public ulong gasPrice { get; set; }
    public BigInteger value { get; set; }
    public long block { get; set; }
    public ITypedArray<byte>? output => _output ??= Output!.ToScriptArray();
    public ITypedArray<byte>? blockHash => _blockHash ??= BlockHash!.BytesToArray().ToScriptArray();
    public int? txIndex { get; set; }
    public ITypedArray<byte>? txHash => _txHash ??= TxHash!.BytesToArray().ToScriptArray();
    public string? error { get; set; }
}
