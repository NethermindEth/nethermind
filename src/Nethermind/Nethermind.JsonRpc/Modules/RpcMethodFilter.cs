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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nethermind.Logging;

[assembly:InternalsVisibleTo("Nethermind.JsonRpc.Test")]

namespace Nethermind.JsonRpc.Modules
{
    internal class RpcMethodFilter : IRpcMethodFilter
    {
        private readonly ILogger _logger;
        private readonly HashSet<string> _filters = new();
        
        private readonly ConcurrentDictionary<string, bool> _methodsCache
            = new();

        public RpcMethodFilter(string filePath, IFileSystem fileSystem, ILogger logger)
        {
            if (!fileSystem.File.Exists(filePath))
            {
                throw new ArgumentNullException(
                    $"{nameof(RpcMethodFilter)} cannot be initialized on a non-existing file {filePath}");
            }

            foreach (string line in fileSystem.File.ReadLines(filePath))
            {
                _filters.Add(line);
            }

            _logger = logger;
        }
        
        public bool AcceptMethod(string methodName)
        {
            if (!_methodsCache.ContainsKey(methodName))
            {
                _methodsCache[methodName] = CheckMethod(methodName);
            }

            return _methodsCache[methodName];
        }

        private bool CheckMethod(string methodName)
        {
            foreach (string filter in _filters)
            {
                if (Regex.IsMatch(methodName.ToLowerInvariant(), filter, RegexOptions.IgnoreCase))
                {
                    if(_logger.IsDebug)
                        _logger.Debug($"{methodName} will be accepted by the JSON RPC filter because of {filter}.");
                    return true;    
                }
            }

            if(_logger.IsDebug)
                _logger.Debug($"{methodName} will not be accepted by the JSON RPC filter (no match).");
            
            return false;
        }
    }
}
