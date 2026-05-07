// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing;

public class CompositeBlockTracer : IBlockTracer, ITracerBag
{
    private readonly List<IBlockTracer> _childTracers = new();
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
        int childCount = _childTracers.Count;
        if (childCount == 0)
        {
            return NullTxTracer.Instance;
        }

        if (childCount == 1)
        {
            return _childTracers[0].StartNewTxTrace(tx);
        }

        ITxTracer? firstTracer = null;
        List<ITxTracer>? tracers = null;
        int tracerCount = 0;

        for (int i = 0; i < childCount; i++)
        {
            IBlockTracer childBlockTracer = _childTracers[i];
            ITxTracer txTracer = childBlockTracer.StartNewTxTrace(tx);
            if (txTracer != NullTxTracer.Instance)
            {
                if (tracerCount == 0)
                {
                    firstTracer = txTracer;
                }
                else
                {
                    tracers ??= new(childCount) { firstTracer! };
                    tracers.Add(txTracer);
                }

                tracerCount++;
            }
        }

        return tracerCount switch
        {
            0 => NullTxTracer.Instance,
            1 => firstTracer!,
            _ => new CompositeTxTracer(tracers!)
        };
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
        IBlockTracer? cached = _parallelSafeTracerCache;
        if (cached is not null &&
            _parallelSafeTracerCacheVersion == _version &&
            _parallelSafeTracerNestedFingerprint == nestedFingerprint)
        {
            return cached;
        }

        IBlockTracer? firstTracer = null;
        List<IBlockTracer>? parallelSafeTracers = null;
        int parallelSafeTracerCount = 0;
        for (int index = 0; index < _childTracers.Count; index++)
        {
            IBlockTracer childTracer = _childTracers[index];
            IBlockTracer parallelSafeTracer = NullBlockTracer.Instance;
            if (childTracer is IParallelSafeBlockTracer)
            {
                parallelSafeTracer = childTracer;
            }
            else if (childTracer is CompositeBlockTracer compositeBlockTracer)
            {
                parallelSafeTracer = compositeBlockTracer.GetParallelSafeTracer();
            }

            if (parallelSafeTracer == NullBlockTracer.Instance)
            {
                continue;
            }

            if (parallelSafeTracerCount == 0)
            {
                firstTracer = parallelSafeTracer;
            }
            else
            {
                parallelSafeTracers ??= new(_childTracers.Count) { firstTracer! };
                parallelSafeTracers.Add(parallelSafeTracer);
            }

            parallelSafeTracerCount++;
        }

        IBlockTracer result = parallelSafeTracerCount switch
        {
            0 => NullBlockTracer.Instance,
            1 => firstTracer!,
            _ => CreateParallelSafeComposite(parallelSafeTracers!)
        };

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
