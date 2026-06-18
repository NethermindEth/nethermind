// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    private readonly Transaction? _transaction;

    // Per-address cumulative storage (matches go-ethereum): captured only at SLOAD/SSTORE, never cleared on call return.
    private readonly Dictionary<Address, Dictionary<string, string>> _storageByAddress = [];

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingMemory = IsTracingFullMemory;
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();

        trace.TxHash = _transaction?.Hash;

        return trace;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        Trace.Gas = gasSpent.SpentGas;
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        base.LoadOperationStorage(address, storageIndex, value);

        RecordStorageSnapshot(address, storageIndex, value);
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        RecordStorageSnapshot(address, storageIndex, newValue);
    }

    private void RecordStorageSnapshot(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        if (CurrentTraceEntry is null)
            return;

        if (!_storageByAddress.TryGetValue(address, out Dictionary<string, string>? contractStorage))
        {
            _storageByAddress[address] = contractStorage = [];
        }

        byte[] bigEndian = new byte[32];
        storageIndex.ToBigEndian(bigEndian);

        contractStorage[bigEndian.ToHexString(true)] = new ZeroPaddedSpan(value, 32 - value.Length, PadDirection.Left)
            .ToArray()
            .ToHexString(true);

        CurrentTraceEntry.Storage = new Dictionary<string, string>(contractStorage);
    }
}
