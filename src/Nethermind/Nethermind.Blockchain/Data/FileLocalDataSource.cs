// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly;

namespace Nethermind.Blockchain.Data
{
    public class FileLocalDataSource<T> : ILocalDataSource<T>, IDisposable
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private T _data;
        private Timer _timer;
        private readonly int _interval;
        private DateTime _lastChange = DateTime.MinValue;
        public string FilePath { get; private set; }

        public FileLocalDataSource(string filePath, IJsonSerializer jsonSerializer, IFileSystem fileSystem, ILogManager logManager, int interval = 500)
        {
            _jsonSerializer = jsonSerializer ?? throw new ArgumentNullException(nameof(jsonSerializer));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logManager?.GetClassLogger<FileLocalDataSource<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            _interval = interval;
            SetupWatcher(filePath.GetApplicationResourcePath());
            LoadFile();
        }

        protected virtual T DefaultValue => default;

        public T Data => _data;

        public event EventHandler Changed;

        public void Dispose()
        {
            _timer?.Dispose();
        }

        private void SetupWatcher(string filePath)
        {
            _data = DefaultValue;
            IFileInfo fileInfo = null;
            try
            {
                fileInfo = _fileSystem.FileInfo.New(filePath);
            }
            catch (ArgumentException) { }
            catch (PathTooLongException) { }
            catch (NotSupportedException) { }

            if (fileInfo is null)
            {
                // file name is not valid
                if (_logger.IsError) _logger.Error($"Invalid file path to watch: {filePath}.");
            }
            else
            {
                // file name is valid
                FilePath = filePath;
                if (_logger.IsInfo) _logger.Info($"Watching file for changes: {filePath}.");
                _timer = new Timer(_interval) { Enabled = true };
                _timer.Elapsed += async (o, e) => await LoadFileAsync();
            }
        }

        private async Task LoadFileAsync()
        {
            try
            {
                await Policy.Handle<JsonException>()
                    .Or<IOException>()
                    .WaitAndRetryAsync(3, CalcRetryIntervals, (exception, i) => ReportRetry(exception))
                    .ExecuteAsync(() =>
                    {
                        LoadFileCore();
                        return Task.CompletedTask;
                    });
            }
            catch (JsonException e)
            {
                ReportJsonError(e);
            }
            catch (IOException e)
            {
                ReportIOError(e);
            }
        }

        private void LoadFile()
        {
            try
            {
                Policy.Handle<JsonException>()
                    .Or<IOException>()
                    .WaitAndRetry(2, CalcRetryIntervals, (exception, i) => ReportRetry(exception))
                    .Execute(() => LoadFileCore());
            }
            catch (JsonException e)
            {
                ReportJsonError(e);
            }
            catch (IOException e)
            {
                ReportIOError(e);
            }
        }

        private static TimeSpan CalcRetryIntervals(int i) => TimeSpan.FromMilliseconds(Math.Pow(10, i - 1));

        private void LoadFileCore()
        {
            DateTime? lastChange = null;

            if (_fileSystem.File.Exists(FilePath))
            {
                var lastWriteTime = _fileSystem.File.GetLastWriteTime(FilePath);
                if (lastWriteTime > _lastChange)
                {
                    if (_logger.IsTrace) _logger.Trace($"Trying to load local data from file: {FilePath} updated on {lastWriteTime:hh:mm:ss:ffff} after last read {_lastChange:hh:mm:ss:ffff}.");
                    using Stream file = _fileSystem.File.OpenRead(FilePath);
                    _data = _jsonSerializer.Deserialize<T>(file);
                    if (_logger.IsDebug) _logger.Debug($"Loaded and deserialized {typeof(T)} from {FilePath}.");
                    lastChange = lastWriteTime;
                }
            }
            else if (!Equals(_data, DefaultValue))
            {
                lastChange = DateTime.Now;
                _data = DefaultValue;
            }

            if (lastChange.HasValue)
            {
                _lastChange = lastChange.Value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ReportJsonError(JsonException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't deserialize {typeof(T)} from {FilePath}. Will not retry any more.", e);
        }

        private void ReportRetry(Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load and deserialize {typeof(T)} from {FilePath}. Retrying...", exception);
        }

        private void ReportIOError(IOException e)
        {
            if (_logger.IsError) _logger.Error($"Couldn't load {typeof(T)} from {FilePath}. Will not retry any more.", e);
        }
    }
}
