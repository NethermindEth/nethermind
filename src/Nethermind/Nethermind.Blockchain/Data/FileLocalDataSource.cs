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
using System.IO.Abstractions;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Newtonsoft.Json;
using Polly;

namespace Nethermind.Blockchain.Data
{
    public class FileLocalDataSource<T> : ILocalDataSource<T>, IDisposable
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private FileSystemWatcher _fileSystemWatcher;
        private readonly string _filePath;
        private T _data;
        private FileSystemEventHandler _handler;

        public FileLocalDataSource(string filePath, IJsonSerializer jsonSerializer, IFileSystem fileSystem, ILogManager logManager)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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
            _fileSystemWatcher.Changed -= _handler;
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
                _handler = async (o, e) => await OnFileChanged(o, e);
                _fileSystemWatcher.Changed += _handler;
            }
            else
            {
                if (_logger.IsError) _logger.Error($"Cannot load data from file: {filePath}.");
            }
        }

        private async Task LoadFileAsync()
        {
            if (_fileSystem.File.Exists(_filePath))
            {
                var start = DateTime.Now;
                try
                {
                    await Policy.Handle<JsonSerializationException>()
                        .Or<IOException>()
                        .WaitAndRetryAsync(4, CalcRetryIntervals, (exception, i) => ReportRetry(start, exception))
                        .ExecuteAsync(() =>
                        {
                            LoadFileCore(start);
                            return Task.CompletedTask;
                        });
                }
                catch (JsonSerializationException e)
                {
                    ReportJsonError(start, e);
                }
                catch (IOException e)
                {
                    ReportIOError(start, e);
                }
                
                _data ??= GetDefaultValue();
            }
            else
            {
                LoadDefaults();
            }
        }

        private void LoadFile()
        {
            if (_fileSystem.File.Exists(_filePath))
            {
                var start = DateTime.Now;
                try
                {
                    Policy.Handle<JsonSerializationException>()
                        .Or<IOException>()
                        .WaitAndRetry(2, CalcRetryIntervals, (exception, i) => ReportRetry(start, exception))
                        .Execute(() =>
                        {
                            LoadFileCore(start);
                        });
                }
                catch (JsonSerializationException e)
                {
                    ReportJsonError(start, e);
                }
                catch (IOException e)
                {
                    ReportIOError(start, e);
                }
                
                _data ??= GetDefaultValue();
            }
            else
            {
                LoadDefaults();
            }
        }
        
        private void LoadDefaults()
        {
            if (_logger.IsWarn) _logger.Error($"Cannot load data from file: {_filePath}, file does not exist.");
            _data = GetDefaultValue();
        }

        private static TimeSpan CalcRetryIntervals(int i) => TimeSpan.FromMilliseconds(Math.Pow(10, i - 1));

        private void LoadFileCore(DateTime start)
        {
            using Stream file = _fileSystem.File.OpenRead(_filePath);
            _data = _jsonSerializer.Deserialize<T>(file);
            if (_logger.IsDebug) _logger.Debug($"Loaded and deserialized {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}.");
        }
        
        private void ReportJsonError(DateTime start, JsonSerializationException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
        }

        private void ReportRetry(DateTime start, Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load and deserialize {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Retrying...", exception);
        }

        private void ReportIOError(DateTime start, IOException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load {typeof(T)} from {_filePath} on {start:hh:mm:ss.ffff}. Will not retry any more.", e);
        }

        private async Task OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_logger.IsInfo) _logger.Info($"Data in file {_filePath} changed.");
            await LoadFileAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
