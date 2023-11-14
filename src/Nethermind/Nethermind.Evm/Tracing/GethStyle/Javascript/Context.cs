// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using Microsoft.ClearScript;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle.Javascript;

public class Context
{
    private IList? _blockHashConverted;
    private IList? _txHashConverted;
    private IList? _inputConverted;
    private IList? _fromConverted;
    private IList? _toConverted;
    private IList? _outputConverted;
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
    public IList? from => _fromConverted ??= From?.Bytes.ToUnTypedScriptArray();
    public IList? to => _toConverted ??= To?.Bytes.ToUnTypedScriptArray();
    public IList? input => _inputConverted ??= Input.ToArray().ToUnTypedScriptArray();
    public long gas { get; set; }
    public long gasUsed { get; set; }
    public dynamic gasPrice => _gasPriceConverted ??= GasPrice.ToBigInteger();
    public dynamic value => _valueConverted ??= Value.ToBigInteger();
    public long block { get; set; }
    public IList? output => _outputConverted ??= Output?.ToUnTypedScriptArray();
    public IList? blockHash => _blockHashConverted ??= BlockHash?.BytesToArray().ToUnTypedScriptArray();
    public int? txIndex { get; set; }
    public IList? txHash => _txHashConverted ??= TxHash?.BytesToArray().ToUnTypedScriptArray();
    public dynamic? error { get; set; } = Undefined.Value;
}
