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
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Json;
using Nethermind.Core2.Store;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.Storage
{
    // Data Class
    public class MemoryStore : IStore
    {
        private readonly Dictionary<Root, BeaconState> _blockStates = new Dictionary<Root, BeaconState>();

        private readonly Dictionary<Checkpoint, BeaconState> _checkpointStates =
            new Dictionary<Checkpoint, BeaconState>();

        private readonly DataDirectory _dataDirectory;
        private readonly IFileSystem _fileSystem;
        private readonly IHeadSelectionStrategy _headSelectionStrategy;
        private readonly IOptionsMonitor<InMemoryConfiguration> _inMemoryConfigurationOptions;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        private readonly Dictionary<ValidatorIndex, LatestMessage> _latestMessages =
            new Dictionary<ValidatorIndex, LatestMessage>();

        private string? _logDirectoryPath;
        private readonly object _logDirectoryPathLock = new object();
        private readonly ILogger _logger;
        private readonly Dictionary<Root, SignedBeaconBlock> _signedBlocks = new Dictionary<Root, SignedBeaconBlock>();
        private readonly StoreAccessor _storeAccessor;
        private const string InMemoryDirectory = "memorystore";

        public MemoryStore(ILogger<MemoryStore> logger,
            IOptionsMonitor<InMemoryConfiguration> inMemoryConfigurationOptions,
            DataDirectory dataDirectory,
            IFileSystem fileSystem,
            IHeadSelectionStrategy headSelectionStrategy,
            StoreAccessor storeAccessor)
        {
            _logger = logger;
            _inMemoryConfigurationOptions = inMemoryConfigurationOptions;
            _dataDirectory = dataDirectory;
            _fileSystem = fileSystem;
            _headSelectionStrategy = headSelectionStrategy;
            _storeAccessor = storeAccessor;
            _jsonSerializerOptions = new JsonSerializerOptions {WriteIndented = true};
            _jsonSerializerOptions.ConfigureNethermindCore2();
            if (_inMemoryConfigurationOptions.CurrentValue.LogBlockJson ||
                inMemoryConfigurationOptions.CurrentValue.LogBlockStateJson)
            {
                _ = GetLogDirectory();
            }
        }

        public Checkpoint BestJustifiedCheckpoint { get; private set; } = Checkpoint.Zero;
        public Checkpoint FinalizedCheckpoint { get; private set; } = Checkpoint.Zero;
        public ulong GenesisTime { get; private set; }
        public bool IsInitialized { get; private set; }
        public Checkpoint JustifiedCheckpoint { get; private set; } = Checkpoint.Zero;
        public ulong Time { get; private set; }

        public async Task<Root> GetAncestorAsync(Root root, Slot slot)
        {
            return await _storeAccessor.GetAncestorAsync(this, root, slot).ConfigureAwait(false);
        }

        public ValueTask<BeaconState> GetBlockStateAsync(Root blockRoot)
        {
            if (!_blockStates.TryGetValue(blockRoot, out BeaconState? state))
            {
                throw new ArgumentOutOfRangeException(nameof(blockRoot), blockRoot, "State not found in store.");
            }

            return new ValueTask<BeaconState>(state!);
        }

        public ValueTask<BeaconState?> GetCheckpointStateAsync(Checkpoint checkpoint, bool throwIfMissing)
        {
            if (!_checkpointStates.TryGetValue(checkpoint, out BeaconState? state))
            {
                if (throwIfMissing)
                {
                    throw new ArgumentOutOfRangeException(nameof(checkpoint), checkpoint,
                        "Checkpoint state not found in store.");
                }
            }

            return new ValueTask<BeaconState?>(state);
        }

        public async IAsyncEnumerable<Root> GetChildKeysAsync(Root parent)
        {
            await Task.CompletedTask;
            IEnumerable<Root> childKeys = _signedBlocks
                .Where(kvp =>
                    kvp.Value.Message.ParentRoot.Equals(parent))
                .Select(kvp => kvp.Key);
            foreach (Root childKey in childKeys)
            {
                yield return childKey;
            }
        }

        public async Task<Root> GetHeadAsync()
        {
            return await _headSelectionStrategy.GetHeadAsync(this).ConfigureAwait(false);
        }

        public ValueTask<LatestMessage?> GetLatestMessageAsync(ValidatorIndex validatorIndex, bool throwIfMissing)
        {
            if (!_latestMessages.TryGetValue(validatorIndex, out LatestMessage? latestMessage))
            {
                if (throwIfMissing)
                {
                    throw new ArgumentOutOfRangeException(nameof(validatorIndex), validatorIndex,
                        "Latest message not found in store.");
                }
            }

            return new ValueTask<LatestMessage?>(latestMessage);
        }

        public ValueTask<SignedBeaconBlock> GetSignedBlockAsync(Root blockRoot)
        {
            if (!_signedBlocks.TryGetValue(blockRoot, out SignedBeaconBlock? signedBeaconBlock))
            {
                throw new ArgumentOutOfRangeException(nameof(blockRoot), blockRoot, "Block not found in store.");
            }

            return new ValueTask<SignedBeaconBlock>(signedBeaconBlock!);
        }

        public async Task InitializeForkChoiceStoreAsync(ulong time, ulong genesisTime, Checkpoint justifiedCheckpoint,
            Checkpoint finalizedCheckpoint, Checkpoint bestJustifiedCheckpoint,
            IDictionary<Root, SignedBeaconBlock> signedBlocks,
            IDictionary<Root, BeaconState> states,
            IDictionary<Checkpoint, BeaconState> checkpointStates)
        {
            if (IsInitialized)
            {
                throw new Exception("Store already initialized.");
            }

            Log.MemoryStoreInitialized(_logger, time, genesisTime, finalizedCheckpoint, null);

            Time = time;
            GenesisTime = genesisTime;
            JustifiedCheckpoint = justifiedCheckpoint;
            FinalizedCheckpoint = finalizedCheckpoint;
            BestJustifiedCheckpoint = bestJustifiedCheckpoint;
            foreach (KeyValuePair<Root, SignedBeaconBlock> kvp in signedBlocks)
            {
                await SetSignedBlockAsync(kvp.Key, kvp.Value);
            }

            foreach (KeyValuePair<Root, BeaconState> kvp in states)
            {
                await SetBlockStateAsync(kvp.Key, kvp.Value);
            }

            foreach (KeyValuePair<Checkpoint, BeaconState> kvp in checkpointStates)
            {
                await SetCheckpointStateAsync(kvp.Key, kvp.Value);
            }

            IsInitialized = true;
        }

        public Task SetBestJustifiedCheckpointAsync(Checkpoint checkpoint)
        {
            BestJustifiedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public async Task SetBlockStateAsync(Root blockHashTreeRoot, BeaconState beaconState)
        {
            _blockStates[blockHashTreeRoot] = beaconState;
            if (_inMemoryConfigurationOptions.CurrentValue.LogBlockJson)
            {
                string logDirectoryPath = GetLogDirectory();
                string fileName = string.Format("state{0:0000}_{1}.json", (int) beaconState.Slot, blockHashTreeRoot);
                string path = _fileSystem.Path.Combine(logDirectoryPath, fileName);
                await using Stream fileStream = _fileSystem.File.OpenWrite(path);
                await JsonSerializer.SerializeAsync(fileStream, beaconState, _jsonSerializerOptions)
                    .ConfigureAwait(false);
            }
        }

        public Task SetCheckpointStateAsync(Checkpoint checkpoint, BeaconState state)
        {
            _checkpointStates[checkpoint] = state;
            return Task.CompletedTask;
        }

        public Task SetFinalizedCheckpointAsync(Checkpoint checkpoint)
        {
            FinalizedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task SetJustifiedCheckpointAsync(Checkpoint checkpoint)
        {
            JustifiedCheckpoint = checkpoint;
            return Task.CompletedTask;
        }

        public Task SetLatestMessageAsync(ValidatorIndex validatorIndex, LatestMessage latestMessage)
        {
            _latestMessages[validatorIndex] = latestMessage;
            return Task.CompletedTask;
        }

        public async Task SetSignedBlockAsync(Root blockHashTreeRoot, SignedBeaconBlock signedBeaconBlock)
        {
            // NOTE: This stores signed block, rather than just the block (or block header) from the spec,
            // because we need to store signed blocks anyway, e.g. to respond to syncing clients.

            _signedBlocks[blockHashTreeRoot] = signedBeaconBlock;
            if (_inMemoryConfigurationOptions.CurrentValue.LogBlockJson)
            {
                string logDirectoryPath = GetLogDirectory();
                string fileName = string.Format("block{0:0000}_{1}.json", (int) signedBeaconBlock.Message.Slot,
                    blockHashTreeRoot);
                string path = _fileSystem.Path.Combine(logDirectoryPath, fileName);
                await using Stream fileStream = _fileSystem.File.OpenWrite(path);
                await JsonSerializer.SerializeAsync(fileStream, signedBeaconBlock, _jsonSerializerOptions)
                    .ConfigureAwait(false);
            }
        }

        public Task SetTimeAsync(ulong time)
        {
            Time = time;
            return Task.CompletedTask;
        }

        private string GetLogDirectory()
        {
            if (_logDirectoryPath == null)
            {
                lock (_logDirectoryPathLock)
                {
                    if (_logDirectoryPath == null)
                    {
                        string basePath = _fileSystem.Path.Combine(_dataDirectory.ResolvedPath, InMemoryDirectory);
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
                            LogDebug.CreatingMemoryStoreLogDirectory(_logger, logDirectoryName,
                                baseDirectoryInfo.FullName, null);
                        IDirectoryInfo logDirectory = baseDirectoryInfo.CreateSubdirectory(logDirectoryName);
                        _logDirectoryPath = logDirectory.FullName;
                    }
                }
            }

            return _logDirectoryPath;
        }
    }
}