// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class StateProviderExtensions
    {
        public static byte[] GetCode(this IStateProvider stateProvider, Address address)
        {
            return stateProvider.GetCode(stateProvider.GetCodeHash(address));
        }

        public static string DumpState(this IStateProvider stateProvider)
        {
            TreeDumper dumper = new();
            stateProvider.Accept(dumper, stateProvider.StateRoot);
            return dumper.ToString();
        }

        public static TrieStats CollectStats(this IStateProvider stateProvider, IKeyValueStore codeStorage, ILogManager logManager)
        {
            TrieStatsCollector collector = new(codeStorage, logManager);
            stateProvider.Accept(collector, stateProvider.StateRoot, new VisitingOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                FullDbScan = true,
            });
            return collector.Stats;
        }
    }
}
