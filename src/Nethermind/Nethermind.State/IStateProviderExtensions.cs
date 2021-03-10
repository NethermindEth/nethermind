//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

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
        
        public static bool HasCode(this IStateProvider stateProvider, Address address)
        {
            return stateProvider.GetCodeHash(address) != Keccak.OfAnEmptyString;
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
            stateProvider.Accept(collector, stateProvider.StateRoot);
            return collector.Stats;
        }
    }
}
