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
            _data = GetDefaultValue();
        }

        protected virtual T GetDefaultValue() => default;

        public T Data => _data;
        
        public event EventHandler Changed;

        public void Dispose()
        {
            _fileSystemWatcher?.Dispose();
        }

        private void SetupWatcher(string filePath)
        {
            string directoryName = Path.GetDirectoryName(_filePath) ?? Environment.CurrentDirectory;
            string fileName = Path.GetFileName(_filePath);
            if (fileName != null)
            {
                _fileSystemWatcher = new FileSystemWatcher(directoryName, fileName);
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
                string json = File.ReadAllText(_filePath);
                try
                {
                    _data = _jsonSerializer.Deserialize<T>(json);
                }
                catch (JsonSerializationException e)
                {
                    if (_logger.IsError) _logger.Error($"Couldn't deserialize {typeof(T)} from {_filePath}.");
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
