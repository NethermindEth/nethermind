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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Logging;

namespace Nethermind.Synchronization.ParallelSync
{
    public interface ISyncModeSelector : IDisposable
    {
        SyncMode Current { get; }
        
        event EventHandler<SyncModeChangedEventArgs> Preparing;
        
        event EventHandler<SyncModeChangedEventArgs> Changing;
        
        event EventHandler<SyncModeChangedEventArgs> Changed;
        
        public static void LogDetailedSyncModeChecks(ILogger logger, string syncType, params (string Name, bool IsSatisfied)[] checks)
        {
            List<string> matched = new();
            List<string> failed = new();

            foreach ((string Name, bool IsSatisfied) check in checks)
            {
                if (check.IsSatisfied)
                {
                    matched.Add(check.Name);
                }
                else
                {
                    failed.Add(check.Name);
                }
            }

            bool result = checks.All(c => c.IsSatisfied);
            string text = $"{(result ? " * " : "   ")}{syncType.PadRight(20)}: yes({string.Join(", ", matched)}), no({string.Join(", ", failed)})";
            logger.Trace(text);
        }
    }
}
