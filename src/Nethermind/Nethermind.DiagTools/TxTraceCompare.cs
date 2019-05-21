/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;

namespace Nethermind.DiagTools
{
    public class TxTraceCompare
    {
        private ILogger _logger = new SimpleConsoleLogger();

        public void Compare(GethLikeTxTrace gethTrace, GethLikeTxTrace nethTrace)
        {
            if (gethTrace.Gas != nethTrace.Gas)
            {
                _logger.Warn($"  gas geth {gethTrace.Gas} != neth {nethTrace.Gas} (diff: {gethTrace.Gas - nethTrace.Gas})");
            }

            byte[] gethReturnValue = gethTrace.ReturnValue;
            byte[] nethReturnValue = nethTrace.ReturnValue;
            if (!Bytes.AreEqual(gethReturnValue, nethReturnValue))
            {
                _logger.Warn($"  return value geth {gethReturnValue.ToHexString()} != neth {nethReturnValue.ToHexString()}");
            }
            
            if (gethTrace.Failed != nethTrace.Failed)
            {
                _logger.Warn($"  failed diff geth {gethTrace.Failed} != neth {nethTrace.Failed}");
            }

            var gethEntries = gethTrace.Entries.ToList();
            var nethEntries = nethTrace.Entries.ToList();
            
            int ixDiff = 0;
            for (int i = 0; i < gethEntries.Count; i++)
            {
//                _logger.Info($"  comparing evm entry {i}");
                var gethEntry = gethEntries[i];
                if (gethEntry.Error != null)
                {
                    ixDiff++;
                    continue;
                }

                int nethIx = i - ixDiff;
                
                string entryDesc = $"ix {i}/{nethIx} pc {gethEntries[i].Pc} op {gethEntries[i].Operation} gas {gethEntries[i].Gas} | ";
                if (nethEntries.Count < nethIx + 1)
                {
                    _logger.Warn($"    neth entry missing");        
                }
                
                var nethEntry = nethEntries[nethIx];
                if (!CompareEntry(gethEntry, nethEntry, entryDesc)) break;
            }
        }

        private bool CompareEntry(GethTxTraceEntry gethEntry, GethTxTraceEntry nethEntry, string entryDesc)
        {
            if (gethEntry.Operation != nethEntry.Operation)
            {
                _logger.Warn($"    {entryDesc} operation geth {gethEntry.Operation} neth {nethEntry.Operation}");
                return false;
            }

            if (gethEntry.Depth != nethEntry.Depth)
            {
                _logger.Warn($"    {entryDesc} depth geth {gethEntry.Depth} neth {nethEntry.Depth}");
                return false;
            }

            if (gethEntry.Gas != nethEntry.Gas)
            {
                _logger.Warn($"    {entryDesc} gas geth {gethEntry.Gas} neth {nethEntry.Gas}");
                return false;
            }

            if (gethEntry.GasCost != nethEntry.GasCost)
            {
                _logger.Warn($"    {entryDesc} gas cost geth {gethEntry.GasCost} neth {nethEntry.GasCost}");
                return false;
            }

            return
                CompareLists(gethEntry.Stack, nethEntry.Stack, "stack", entryDesc) &&
                CompareLists(gethEntry.Memory, nethEntry.Memory, "memory", entryDesc) &&
                CompareStorage(gethEntry, nethEntry, entryDesc);
        }

        private bool CompareLists(List<string> gethList, List<string> nethList, string listName, string entryDesc)
        {
            if (gethList.Count != nethList.Count)
            {
                _logger.Warn($"    {entryDesc} {listName} lengths differ geth {gethList.Count} neth {nethList.Count}");
                return false;
            }

            for (int i = 0; i < gethList.Count; i++)
            {
                if (gethList[i] != nethList[i])
                {
                    _logger.Warn($"    {entryDesc} {listName} values differ at index {i} geth {gethList[i]} neth {nethList[i]}");
                    return false;
                }
            }

            return true;
        }

        private bool CompareStorage(GethTxTraceEntry gethEntry, GethTxTraceEntry nethEntry, string entryDesc)
        {
            foreach (KeyValuePair<string,string> keyValuePair in gethEntry.Storage)
            {
                if (!nethEntry.Storage.ContainsKey(keyValuePair.Key))
                {
                    _logger.Warn($"   {entryDesc} missing neth storage {keyValuePair.Key}");
                    return false;
                }
            }
            
            foreach (KeyValuePair<string,string> keyValuePair in nethEntry.Storage)
            {
                if (!gethEntry.Storage.ContainsKey(keyValuePair.Key))
                {
                    _logger.Warn($"   {entryDesc} extra neth storage {keyValuePair.Key}");
                    return false;
                }
            }
            
            foreach (KeyValuePair<string,string> keyValuePair in nethEntry.Storage)
            {
                if(nethEntry.Storage[keyValuePair.Key] != gethEntry.Storage[keyValuePair.Key])
                {
                    _logger.Warn($"   {entryDesc} different storage values at {keyValuePair.Key} geth {gethEntry.Storage[keyValuePair.Key]} neth {nethEntry.Storage[keyValuePair.Key]}");
                    return false;
                }
            }

            return true;
        }
    }
}