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
// 

using System;
using System.IO;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Polly;

namespace Nethermind.Blockchain.Data
{
    public class FileLocalDataSource<T> : ILocalDataSource<T>, IDisposable
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;
        private FileSystemWatcher _fileSystemWatcher;
        private readonly string _filePath;
        private T _data;

        public FileLocalDataSource(string filePath, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _filePath = filePath.GetApplicationResourcePath();
            _logger = logManager?.GetClassLogger<FileLocalDataSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            SetupWatcher(filePath);
            LoadFile();
        }

        protected virtual T GetDefaultValue() => default;

        public T Data => _data;
        
        public event EventHandler Changed;

        public void Dispose()
        {
            _fileSystemWatcher.Changed -= OnFileChanged;
            _fileSystemWatcher?.Dispose();
        }

        private void SetupWatcher(string filePath)
        {
            string directoryName = Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory;
            string fileName = Path.GetFileName(_filePath);
            if (fileName != null)
            {
                _fileSystemWatcher = new FileSystemWatcher(directoryName, fileName)
                {
                    EnableRaisingEvents = true
                };
                _fileSystemWatcher.Changed += OnFileChanged;
            }
            else
            {
                if (_logger.IsError) _logger.Error($"Cannot load data from file: {filePath}.");
            }
        }

        private void LoadFile()
        {
            if (File.Exists(_filePath))
            {
                var start = DateTime.Now;
                try
                {
                    Policy.Handle<JsonSerializationException>()
                        .Or<IOException>()
                        .WaitAndRetry(4, i => TimeSpan.FromMilliseconds(Math.Pow(10, i - 1)), (exception, i) =>
                        {
                            if (_logger.IsError) _logger.Error($"Couldn't load and deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Retrying...", exception);
                        })
                        .Execute(() =>
                        {
                            using var file = File.OpenRead(_filePath);
                            _data = _jsonSerializer.Deserialize<T>(file);
                            if (_logger.IsDebug) _logger.Debug($"Loaded and deserialized {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}.");
                        });
                }
                catch (JsonSerializationException e)
                {
                    if (_logger.IsError) _logger.Error($"Couldn't deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
                }
                catch (IOException e)
                {
                    if (_logger.IsError) _logger.Error($"Couldn't load {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
                }
            }
            else
            {
                _data = GetDefaultValue();
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_logger.IsInfo) _logger.Info($"Data in file {_filePath} changed.");
            LoadFile();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
