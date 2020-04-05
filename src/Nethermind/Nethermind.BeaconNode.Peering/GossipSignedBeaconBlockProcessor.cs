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

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Peering
{
    public class GossipSignedBeaconBlockProcessor : QueueProcessorBase<SignedBeaconBlock>
    {
        private const int MaximumQueue = 1024;
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MothraConfiguration> _mothraConfigurationOptions;
        private readonly IFileSystem _fileSystem;
        private readonly IForkChoice _forkChoice;
        private readonly IStore _store;
        private readonly DataDirectory _dataDirectory;
        private readonly PeerManager _peerManager;
        private string? _logDirectoryPath;
        private readonly object _logDirectoryPathLock = new object();

        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public GossipSignedBeaconBlockProcessor(ILogger<GossipSignedBeaconBlockProcessor> logger,
            IOptionsMonitor<MothraConfiguration> mothraConfigurationOptions,
            IFileSystem fileSystem,
            IForkChoice forkChoice,
            IStore store,
            DataDirectory dataDirectory,
            PeerManager peerManager)
            : base(logger, MaximumQueue)
        {
            _logger = logger;
            _mothraConfigurationOptions = mothraConfigurationOptions;
            _fileSystem = fileSystem;
            _forkChoice = forkChoice;
            _store = store;
            _dataDirectory = dataDirectory;
            _peerManager = peerManager;

            _jsonSerializerOptions = new JsonSerializerOptions {WriteIndented = true};
            _jsonSerializerOptions.ConfigureNethermindCore2();
            if (_mothraConfigurationOptions.CurrentValue.LogSignedBeaconBlockJson)
            {
                _ = GetLogDirectory();
            }
        }

        protected override async Task ProcessItemAsync(SignedBeaconBlock signedBeaconBlock)
        {
            try
            {
                if (_logger.IsDebug())
                    LogDebug.ProcessGossipSignedBeaconBlock(_logger, signedBeaconBlock.Message, null);

                if (_mothraConfigurationOptions.CurrentValue.LogSignedBeaconBlockJson)
                {
                    string logDirectoryPath = GetLogDirectory();
                    string fileName = string.Format("signedblock{0:0000}_{1}.json",
                        (int) signedBeaconBlock.Message.Slot,
                        signedBeaconBlock.Signature.ToString().Substring(0, 10));
                    string path = _fileSystem.Path.Combine(logDirectoryPath, fileName);
                    using (Stream fileStream = _fileSystem.File.OpenWrite(path))
                    {
                        await JsonSerializer.SerializeAsync(fileStream, signedBeaconBlock, _jsonSerializerOptions)
                            .ConfigureAwait(false);
                    }
                }

                // Update the most recent slot seen (even if we can't add it to the chain yet, e.g. if we are missing prior blocks)
                // Note: a peer could lie and send a signed block that isn't part of the chain (but it could lie on status as well)
                _peerManager.UpdateMostRecentSlot(signedBeaconBlock.Message.Slot);

                await _forkChoice.OnBlockAsync(_store, signedBeaconBlock).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.ProcessGossipSignedBeaconBlockError(_logger, signedBeaconBlock.Message, ex.Message, ex);
            }
        }

        public void Enqueue(SignedBeaconBlock signedBeaconBlock)
        {
            ChannelWriter.TryWrite(signedBeaconBlock);
        }
        
        private string GetLogDirectory()
        {
            if (_logDirectoryPath == null)
            {
                lock (_logDirectoryPathLock)
                {
                    if (_logDirectoryPath == null)
                    {
                        string basePath = _fileSystem.Path.Combine(_dataDirectory.ResolvedPath, MothraPeeringWorker.MothraDirectory);
                        IDirectoryInfo baseDirectoryInfo = _fileSystem.DirectoryInfo.FromDirectoryName(basePath);
                        if (!baseDirectoryInfo.Exists)
                        {
                            baseDirectoryInfo.Create();
                        }

                        IDirectoryInfo[] existingLogDirectories = baseDirectoryInfo.GetDirectories("log*");
                        int existingSuffix = existingLogDirectories.Select(x =>
                            {
                                if (int.TryParse(x.Name.Substring(3), out int suffix))
                                {
                                    return suffix;
                                }

                                return 0;
                            })
                            .DefaultIfEmpty()
                            .Max();
                        int newSuffix = existingSuffix + 1;
                        string logDirectoryName = $"log{newSuffix:0000}";

                        if (_logger.IsDebug())
                            LogDebug.CreatingMothraLogDirectory(_logger, logDirectoryName, baseDirectoryInfo.FullName,
                                null);
                        IDirectoryInfo logDirectory = baseDirectoryInfo.CreateSubdirectory(logDirectoryName);
                        _logDirectoryPath = logDirectory.FullName;
                    }
                }
            }

            return _logDirectoryPath;
        }


    }
}