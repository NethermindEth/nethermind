// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;

public class Context
{
    private ITypedArray<byte>? _blockHashConverted;
    private ITypedArray<byte>? _txHashConverted;
    private ITypedArray<byte>? _inputConverted;
    private ITypedArray<byte>? _fromConverted;
    private ITypedArray<byte>? _toConverted;
    private ITypedArray<byte>? _outputConverted;
    private dynamic? _valueConverted;
    private dynamic? _gasPriceConverted;
    private Address? _from;
    private Address? _to;
    private ReadOnlyMemory<byte> _input;
    private byte[]? _output;
    private Hash256? _blockHash;
    private Hash256? _txHash;
    private UInt256 _value;
    private UInt256 _gasPrice;

    public Address? From
    {
        get => _from;
        set
        {
            _from = value;
            _fromConverted = null;
        }
    }

    public Address? To
    {
        get => _to;
        set
        {
            _to = value;
            _toConverted = null;
        }
    }

    public ReadOnlyMemory<byte> Input
    {
        get => _input;
        set
        {
            _input = value;
            _inputConverted = null;
        }
    }

    public byte[]? Output
    {
        get => _output;
        set
        {
            _output = value;
            _outputConverted = null;
        }
    }

    public Hash256? BlockHash
    {
        get => _blockHash;
        set
        {
            _blockHash = value;
            _blockHashConverted = null;
        }
    }

    public Hash256? TxHash
    {
        get => _txHash;
        set
        {
            _txHash = value;
            _txHashConverted = null;
        }
    }

    public UInt256 Value
    {
        get => _value;
        set
        {
            _value = value;
            _valueConverted = null;
        }
    }

    public UInt256 GasPrice
    {
        get => _gasPrice;
        set
        {
            _gasPrice = value;
            _gasPriceConverted = null;
        }
    }

    public string type { get; set; } = null!;
    public ITypedArray<byte>? from => _fromConverted ??= From?.Bytes.ToTypedScriptArray();
    public ITypedArray<byte>? to => _toConverted ??= To?.Bytes.ToTypedScriptArray();
    public ITypedArray<byte>? input => _inputConverted ??= Input.ToArray().ToTypedScriptArray();
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public IJavaScriptObject gasPrice => _gasPriceConverted ??= GasPrice.ToBigInteger();
    public IJavaScriptObject value => _valueConverted ??= Value.ToBigInteger();
    public long block { get; set; }
    public ITypedArray<byte>? output => _outputConverted ??= Output?.ToTypedScriptArray();
    public ITypedArray<byte>? blockHash => _blockHashConverted ??= BlockHash?.BytesToArray().ToTypedScriptArray();
    public int? txIndex { get; set; }
    public ITypedArray<byte>? txHash => _txHashConverted ??= TxHash?.BytesToArray().ToTypedScriptArray();
    public dynamic? error { get; set; } = Undefined.Value;
}
