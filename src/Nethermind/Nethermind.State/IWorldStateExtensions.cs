// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State
{
    public static class WorldStateExtensions
    {
        public static byte[] GetCode(this IWorldState stateProvider, Address address)
        {
            AccountStruct account = stateProvider.GetAccount(address);
            return !account.HasCode ? Array.Empty<byte>() : stateProvider.GetCode(account.CodeHash);
        }

        public static void InsertCode(this IWorldState worldState, Address address, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false)
        {
            Hash256 codeHash = code.Length == 0 ? Keccak.OfAnEmptyString : Keccak.Compute(code.Span);
            worldState.InsertCode(address, codeHash, code, spec, isGenesis);
        }

        public static string DumpState(this IWorldState stateProvider)
        {
            TreeDumper dumper = new();
            stateProvider.Accept(dumper, stateProvider.StateRoot);
            return dumper.ToString();
        }

        public static TrieStats CollectStats(this IWorldState stateProvider, IKeyValueStore codeStorage, ILogManager logManager)
        {
            TrieStatsCollector collector = new(codeStorage, logManager);
            stateProvider.Accept(collector, stateProvider.StateRoot, new VisitingOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                FullScanMemoryBudget = 16.GiB(), // Gonna guess that if you are running this, you have a decent setup.
            });
            return collector.Stats;
        }
    }
}
