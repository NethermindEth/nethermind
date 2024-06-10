// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;

namespace Nethermind.Facade.Simulate;

public class SimulateBlockTracer(bool isTracingLogs, bool includeFullTxData, ISpecProvider spec) : BlockTracer
{
    private readonly List<SimulateTxMutatorTracer> _txTracers = new();

    private Block _currentBlock = null!;
    public List<SimulateBlockResult> Results { get; } = new();

    public override void StartNewBlockTrace(Block block)
    {
        _txTracers.Clear();
        _currentBlock = block;
    }

    public override ITxTracer StartNewTxTrace(Transaction? tx)
    {

        if (tx?.Hash is not null)
        {
            ulong txIndex = (ulong)_txTracers.Count;
            SimulateTxMutatorTracer result = new(isTracingLogs, tx.Hash, (ulong)_currentBlock.Number,
                _currentBlock.Hash, txIndex);
            _txTracers.Add(result);
            return result;
        }

        return NullTxTracer.Instance;
    }

    public override void EndBlockTrace()
    {
        SimulateBlockResult? result = new(_currentBlock, includeFullTxData, spec)
        {
            Calls = _txTracers.Select(t => t.TraceResult).ToList(),
        };
        if (_currentBlock.Header.ExcessBlobGas is not null)
        {
            if (!BlobGasCalculator.TryCalculateBlobGasPricePerUnit(_currentBlock.Header.ExcessBlobGas.Value,
                    out UInt256 blobGasPricePerUnit))
            {
                result.BlobBaseFee = blobGasPricePerUnit;
            }
        }

        Results.Add(result);
    }
}
