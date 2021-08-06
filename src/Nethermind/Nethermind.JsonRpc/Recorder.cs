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

using System;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Logging;

namespace Nethermind.JsonRpc
{
    internal class Recorder
    {
        private string _recorderBaseFilePath;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private int _recorderFileCounter;
        private string _currentRecorderFilePath;
        private int _currentRecorderFileLength;
        private bool _isEnabled = true;
        private object _recorderSync = new();

        public Recorder(string basePath, IFileSystem fileSystem, ILogger logger)
        {
            _recorderBaseFilePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            using Stream stream =_fileSystem.File.Create(_currentRecorderFilePath);
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
                    _currentRecorderFileLength += data.Length;
                    if (_currentRecorderFileLength > 4 * 1024 * 2014)
                    {
                        CreateNewRecorderFile();
                    }

                    string singleLineRequest = data.Replace(Environment.NewLine, "");
                    _fileSystem.File.AppendAllText(_currentRecorderFilePath, singleLineRequest + Environment.NewLine);
                }
            }
        }
    }
}
