// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Core;

/// <summary>
/// A Block specifically for receipt recovery. Does not contain any of the fields that are not needed for that.
/// Retain span from DB as memory and must be explicitly disposed.
/// </summary>
[DebuggerDisplay("{Hash} ({Number})")]
public class ReceiptRecoveryBlock
{
    public ReceiptRecoveryBlock(Block block)
    {
        Header = block.Header;
        _transactions = block.Transactions;
    }

    public ReceiptRecoveryBlock(BlockHeader header, Memory<byte> data)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        Data = data;
    }

    private Memory<byte> Data { get; set; }
    private Memory<byte>[] TransactionData { get; set; }


    private Transaction[]? _transactions = null;

    public BlockHeader Header { get; }
    public int TransactionCount => _transactions?.Length ?? TransactionData.Length;

    public Transaction GetTransaction(int idx)
    {
        if (_transactions != null)
        {
            return _transactions[idx];
        }

        Rlp.ValueDecoderContext decoderContext = new(TransactionData[idx].Span);

        return TxDecoder.Instance.Decode(ref decoderContext);
    }

    public Keccak? Hash => Header.Hash; // do not add setter here
    public long Number => Header.Number; // do not add setter here

    public void Dispose()
    {
    }
}
