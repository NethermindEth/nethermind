// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Nethermind.Logging;

[assembly: InternalsVisibleTo("Nethermind.JsonRpc.Test")]

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
                    if (_logger.IsDebug)
                        _logger.Debug($"{methodName} will be accepted by the JSON RPC filter because of {filter}.");
                    return true;
                }
            }

            if (_logger.IsDebug)
                _logger.Debug($"{methodName} will not be accepted by the JSON RPC filter (no match).");

            return false;
        }
    }
}
