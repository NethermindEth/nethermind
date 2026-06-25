// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Exceptions;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Consensus.Processing;

public readonly record struct GasValidationResult(
    ulong BlockGasUsed,
    ulong BlockStateGasUsed,
    IntrinsicGas<EthereumGasPolicy> IntrinsicGas,
    InvalidBlockException? Exception);

public sealed class GasValidationResultSlot
{
    // Lock instance: Monitor.Wait/PulseAll require an object reference, not the
    // System.Threading.Lock class (which is incompatible with Wait/Pulse).
    private readonly object _sync = new();
    private GasValidationResult _result;
    private Exception? _exception;
    private bool _completed;

    public void Reset()
    {
        lock (_sync)
        {
            _result = default;
            _exception = null;
            _completed = false;
        }
    }

    public bool TrySetResult(in GasValidationResult result)
    {
        lock (_sync)
        {
            if (_completed)
            {
                return false;
            }

            _result = result;
            _exception = null;
            _completed = true;
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    public bool TrySetCanceled()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return false;
            }

            _result = default;
            _exception = new TaskCanceledException();
            _completed = true;
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    public GasValidationResult GetResult()
    {
        lock (_sync)
        {
            while (!_completed)
            {
                Monitor.Wait(_sync);
            }

            if (_exception is not null)
            {
                ExceptionDispatchInfo.Capture(_exception).Throw();
            }

            return _result;
        }
    }
}
