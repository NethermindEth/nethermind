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
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
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
    public class SignedBeaconBlockProcessor : QueueProcessorBase<(SignedBeaconBlock signedBeaconBlock, string? peerId)>
    {
        private readonly DataDirectory _dataDirectory;
        private readonly IFileSystem _fileSystem;
        private readonly IForkChoice _forkChoice;

        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private string? _logDirectoryPath;
        private readonly object _logDirectoryPathLock = new object();
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MothraConfiguration> _mothraConfigurationOptions;
        private readonly PeerManager _peerManager;
        private readonly IStore _store;
        private const int MaximumQueue = 1024;

        public SignedBeaconBlockProcessor(ILogger<SignedBeaconBlockProcessor> logger,
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

        public void Enqueue(SignedBeaconBlock signedBeaconBlock, string peerId)
        {
            EnqueueItem((signedBeaconBlock, peerId));
        }

        public void EnqueueGossip(SignedBeaconBlock signedBeaconBlock)
        {
            EnqueueItem((signedBeaconBlock, null));
        }

        protected override async Task ProcessItemAsync((SignedBeaconBlock signedBeaconBlock, string? peerId) item)
        {
            try
            {
                if (_logger.IsDebug())
                    LogDebug.ProcessSignedBeaconBlock(_logger, item.signedBeaconBlock.Message, item.peerId ?? "gossip",
                        null);

                if (_mothraConfigurationOptions.CurrentValue.LogSignedBeaconBlockJson)
                {
                    string logDirectoryPath = GetLogDirectory();
                    string fileName = string.Format("signedblock{0:0000}_{1}{2}.json",
                        (int) item.signedBeaconBlock.Message.Slot,
                        item.signedBeaconBlock.Signature.ToString().Substring(0, 10),
                        item.peerId == null ? "" : "_" + item.peerId);
                    string path = _fileSystem.Path.Combine(logDirectoryPath, fileName);
                    using (Stream fileStream = _fileSystem.File.OpenWrite(path))
                    {
                        await JsonSerializer.SerializeAsync(fileStream, item.signedBeaconBlock, _jsonSerializerOptions)
                            .ConfigureAwait(false);
                    }
                }

                // Update the most recent slot seen (even if we can't add it to the chain yet, e.g. if we are missing prior blocks)
                // Note: a peer could lie and send a signed block that isn't part of the chain (but it could lie on status as well)
                _peerManager.UpdateMostRecentSlot(item.signedBeaconBlock.Message.Slot);

                await _forkChoice.OnBlockAsync(_store, item.signedBeaconBlock).ConfigureAwait(false);

                // NOTE: We don't know peer from Mothra for gossipped blocks, so can't penalise if there is an issue,
                // however we could do for RPC responses if we wanted to.

                // TODO: Handling for blocks we are missing parents for, i.e. BeaconBlocksByRoot
            }
            catch (Exception ex)
            {
                if (_logger.IsError())
                    Log.ProcessSignedBeaconBlockError(_logger, item.signedBeaconBlock.Message, ex.Message, ex);
            }
        }

        private string GetLogDirectory()
        {
            if (_logDirectoryPath == null)
            {
                lock (_logDirectoryPathLock)
                {
                    if (_logDirectoryPath == null)
                    {
                        string basePath = _fileSystem.Path.Combine(_dataDirectory.ResolvedPath,
                            MothraPeeringWorker.MothraDirectory);
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