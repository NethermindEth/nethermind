// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class Context
{
    private IList? _blockHash;
    private IList? _txHash;
    private IList? _input;
    private IList? _from;
    private IList? _to;
    private IList? _output;
    private dynamic? _value;
    public Address? From { get; set; }
    public Address? To { get; set; }
    public ReadOnlyMemory<byte> Input { get; set; }
    public byte[]? Output { get; set; }
    public Hash256? BlockHash { get; set; }
    public Hash256? TxHash { get; set; }
    public UInt256 Value { get; set; }

    public string type { get; set; } = null!;
    public IList? from => _from ??= From?.Bytes.ToUnTypedScriptArray();
    public IList? to => _to ??= To?.Bytes.ToUnTypedScriptArray();
    public IList? input => _input ??= Input.ToArray().ToUnTypedScriptArray();
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public ulong gasPrice { get; set; }
    public dynamic value => _value ??= Value.ToBigInteger();
    public long block { get; set; }
    public IList? output => _output ??= Output?.ToUnTypedScriptArray();
    public IList? blockHash => _blockHash ??= BlockHash?.BytesToArray().ToUnTypedScriptArray();
    public int? txIndex { get; set; }
    public IList? txHash => _txHash ??= TxHash?.BytesToArray().ToUnTypedScriptArray();
    public string? error { get; set; }
}
