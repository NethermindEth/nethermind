// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.JsonRpc
{
    internal class Recorder
    {
        // Counted in characters of the recorded strings, not in bytes written.
        private const int MaxFileLength = 4 * 1024 * 1024;

        private readonly string _recorderBaseFilePath;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private int _recorderFileCounter;
        private string _currentRecorderFilePath;
        private int _currentRecorderFileLength;
        private bool _isEnabled = true;
        private readonly Lock _recorderSync = new();

        public Recorder(string basePath, IFileSystem fileSystem, ILogger logger)
        {
            _recorderBaseFilePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logger;
            CreateNewRecorderFile();
        }

        private void CreateNewRecorderFile()
        {
            if (!_recorderBaseFilePath.Contains("{counter}"))
            {
                if (_logger.IsError) _logger.Error("Disabling recorder because of an invalid recorder file path - it should contain '{counter}'");
                _isEnabled = false;
                return;
            }

            _currentRecorderFilePath = _recorderBaseFilePath.Replace("{counter}", _recorderFileCounter.ToString());
            using Stream stream = _fileSystem.File.Create(_currentRecorderFilePath);
            _recorderFileCounter++;
            _currentRecorderFileLength = 0;
        }

        public void RecordRequest(string request) => Record(request);

        public void RecordResponse(string result) => Record(result);

        private void Record(string data)
        {
            if (_isEnabled)
            {
                lock (_recorderSync)
                {
                    if (_currentRecorderFileLength > 0 && _currentRecorderFileLength + data.Length > MaxFileLength)
                    {
                        CreateNewRecorderFile();
                    }

                    string singleLineRequest = data.Replace(Environment.NewLine, string.Empty);
                    _fileSystem.File.AppendAllText(_currentRecorderFilePath, singleLineRequest + Environment.NewLine);
                    _currentRecorderFileLength += data.Length;
                }
            }
        }
    }
}
