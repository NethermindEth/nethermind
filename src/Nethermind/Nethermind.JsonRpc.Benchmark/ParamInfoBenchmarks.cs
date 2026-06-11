// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Benchmark
{
    public class ParamInfoBenchmarks
    {
        // ReSharper disable once UnassignedField.Global
        // ReSharper disable once MemberCanBePrivate.Global
        [ParamsSource(nameof(Scenarios))] public MethodInfo MethodInfo = null!;

        // ReSharper disable once MemberCanBePrivate.Global
        public MethodInfo[] Scenarios { get; } = new MethodInfo[2];
        private Dictionary<MethodInfo, ParameterInfo[]> _paramsCache = [];
        private ConcurrentDictionary<MethodInfo, ParameterInfo[]> _concurrentParamsCache = new();

        public ParamInfoBenchmarks()
        {
            Scenarios[0] = typeof(EthRpcModule).GetMethod("eth_getStorageAt", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(nameof(EthRpcModule), "eth_getStorageAt");
            Scenarios[1] = typeof(EthRpcModule).GetMethod("eth_blockNumber", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMethodException(nameof(EthRpcModule), "eth_blockNumber");
        }

        [Benchmark(Baseline = true)]
        public ParameterInfo[] Current() => MethodInfo.GetParameters();

        [Benchmark]
        public ParameterInfo[] Cached()
        {
            lock (_paramsCache)
            {
                if (!_paramsCache.ContainsKey(MethodInfo))
                {
                    _paramsCache[MethodInfo] = MethodInfo.GetParameters();
                }
            }

            lock (_paramsCache)
            {
                return _paramsCache[MethodInfo];
            }
        }

        [Benchmark]
        public ParameterInfo[] Cached_concurrent() =>
            // ReSharper disable once InconsistentlySynchronizedField
            _concurrentParamsCache.GetOrAdd(MethodInfo, mi => mi.GetParameters());
    }
}
