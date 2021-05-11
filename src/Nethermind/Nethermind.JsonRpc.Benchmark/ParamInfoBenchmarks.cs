//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
        [ParamsSource(nameof(Scenarios))] public MethodInfo MethodInfo;

        // ReSharper disable once MemberCanBePrivate.Global
        public MethodInfo[] Scenarios { get; } = new MethodInfo[2];
        private Dictionary<MethodInfo, ParameterInfo[]> _paramsCache = new Dictionary<MethodInfo, ParameterInfo[]>();
        private ConcurrentDictionary<MethodInfo, ParameterInfo[]> _concurrentParamsCache = new ConcurrentDictionary<MethodInfo, ParameterInfo[]>();

        public ParamInfoBenchmarks()
        {
            Scenarios[0] = typeof(EthRpcModule).GetMethod("eth_getStorageAt", BindingFlags.Public | BindingFlags.Instance);
            Scenarios[1] = typeof(EthRpcModule).GetMethod("eth_blockNumber", BindingFlags.Public | BindingFlags.Instance);
        }

        [Benchmark(Baseline = true)]
        public ParameterInfo[] Current()
        {
            return MethodInfo.GetParameters();
        }

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
        public ParameterInfo[] Cached_concurrent()
        {
            // ReSharper disable once InconsistentlySynchronizedField
            return _concurrentParamsCache.GetOrAdd(MethodInfo, mi => mi.GetParameters());
        }
    }
}
