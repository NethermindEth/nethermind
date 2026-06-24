// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class CompositeBlockTracer : IBlockTracer
{
    private readonly List<IBlockTracer> _childTracers = [];
    private IBlockTracer? _parallelSafeTracerCache;
    private int _parallelSafeTracerCacheVersion = -1;
    private int _parallelSafeTracerNestedFingerprint;
    private int _version;

    public bool IsTracingRewards { get; private set; }

    public void EndTxTrace()
    {
        foreach (IBlockTracer childTracer in _childTracers)
        {
            childTracer.EndTxTrace();
        }
    }

    public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
    {
        for (int index = 0; index < _childTracers.Count; index++)
        {
            IBlockTracer childTracer = _childTracers[index];
            if (childTracer.IsTracingRewards)
            {
                childTracer.ReportReward(author, rewardType, rewardValue);
            }
        }
    }

    public void StartNewBlockTrace(Block block)
    {
        for (int index = 0; index < _childTracers.Count; index++)
        {
            IBlockTracer childTracer = _childTracers[index];
            childTracer.StartNewBlockTrace(block);
        }
    }

    public ITxTracer StartNewTxTrace(Transaction? tx)
    {
        ITxTracer? singleTracer = null;
        List<ITxTracer>? tracers = null;

        for (int i = 0; i < _childTracers.Count; i++)
        {
            ITxTracer txTracer = _childTracers[i].StartNewTxTrace(tx);

            if (txTracer == NullTxTracer.Instance)
            {
                continue;
            }

            if (singleTracer is null)
            {
                singleTracer = txTracer;
            }
            else
            {
                tracers ??= [singleTracer];
                tracers.Add(txTracer);
            }
        }

        return tracers is not null
            ? new CompositeTxTracer(tracers)
            : singleTracer ?? NullTxTracer.Instance;
    }

    public void EndBlockTrace()
    {
        for (int index = 0; index < _childTracers.Count; index++)
        {
            IBlockTracer childTracer = _childTracers[index];
            childTracer.EndBlockTrace();
        }
    }

    public void Add(IBlockTracer tracer)
    {
        _childTracers.Add(tracer);
        IsTracingRewards |= tracer.IsTracingRewards;
        _version++;
    }

    public void AddRange(params IBlockTracer[] tracers)
    {
        _childTracers.AddRange(tracers);
        for (int i = 0; i < tracers.Length; i++)
        {
            IsTracingRewards |= tracers[i].IsTracingRewards;
        }
        _version++;
    }

    public void Remove(IBlockTracer tracer)
    {
        _childTracers.Remove(tracer);
        IsTracingRewards = false;
        for (int i = 0; i < _childTracers.Count; i++)
        {
            IsTracingRewards |= _childTracers[i].IsTracingRewards;
        }
        _version++;
    }

    public IBlockTracer GetTracer() =>
        _childTracers.Count switch
        {
            0 => NullBlockTracer.Instance,
            1 => _childTracers[0],
            _ => this
        };

    public IBlockTracer GetParallelSafeTracer()
    {
        int nestedFingerprint = GetNestedVersionFingerprint();
        if (_parallelSafeTracerCache is { } cached &&
            _parallelSafeTracerCacheVersion == _version &&
            _parallelSafeTracerNestedFingerprint == nestedFingerprint)
        {
            return cached;
        }

        IBlockTracer? singleTracer = null;
        List<IBlockTracer>? tracers = null;

        for (int i = 0; i < _childTracers.Count; i++)
        {
            IBlockTracer tracer = _childTracers[i] switch
            {
                IParallelSafeBlockTracer parallelSafe => parallelSafe,
                CompositeBlockTracer composite => composite.GetParallelSafeTracer(),
                _ => NullBlockTracer.Instance
            };

            if (tracer == NullBlockTracer.Instance)
            {
                continue;
            }

            if (singleTracer is null)
            {
                singleTracer = tracer;
            }
            else
            {
                tracers ??= [singleTracer];
                tracers.Add(tracer);
            }
        }

        IBlockTracer result = tracers is not null
            ? CreateParallelSafeComposite(tracers)
            : singleTracer ?? NullBlockTracer.Instance;

        _parallelSafeTracerCache = result;
        _parallelSafeTracerCacheVersion = _version;
        _parallelSafeTracerNestedFingerprint = nestedFingerprint;
        return result;
    }

    private int GetNestedVersionFingerprint()
    {
        int fingerprint = 0;
        for (int i = 0; i < _childTracers.Count; i++)
        {
            if (_childTracers[i] is CompositeBlockTracer compositeBlockTracer)
            {
                fingerprint = HashCode.Combine(fingerprint, compositeBlockTracer._version, compositeBlockTracer.GetNestedVersionFingerprint());
            }
        }

        return fingerprint;
    }

    private static CompositeBlockTracer CreateParallelSafeComposite(List<IBlockTracer> parallelSafeTracers)
    {
        CompositeBlockTracer parallelSafeCompositeBlockTracer = new();
        parallelSafeCompositeBlockTracer._childTracers.AddRange(parallelSafeTracers);
        for (int index = 0; index < parallelSafeTracers.Count; index++)
        {
            parallelSafeCompositeBlockTracer.IsTracingRewards |= parallelSafeTracers[index].IsTracingRewards;
        }

        return parallelSafeCompositeBlockTracer;
    }
}
