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
// 

using System;
using System.IO;
using System.IO.Abstractions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Pipeline.Publishers
{
    public class LogPublisher<TIn, TOut> : IPipelineElement<TIn, TOut>
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly string _logsDir;
        private const string FolderData = "pipeline";
        private const string FileName = "pipeline_data.txt";
        public Action<TOut> Emit { get; set; }

        public LogPublisher(IJsonSerializer jsonSerializer, ILogManager logger, IFileSystem fileSystem = null)
        {
            _logger = logger.GetClassLogger<LogPublisher<TIn, TOut>>();
            _jsonSerializer = jsonSerializer;
            _fileSystem = fileSystem ?? new FileSystem();
            _logsDir = FolderData.GetApplicationResourcePath();
            if (!_fileSystem.Directory.Exists(_logsDir))
            {
                _fileSystem.Directory.CreateDirectory(_logsDir);
            }
        }
        
        public void SubscribeToData(TIn data)
        {
            if (_logger.IsWarn) _logger.Warn(_jsonSerializer.Serialize(data));
            _fileSystem.File.AppendAllText(Path.Combine(_logsDir, FileName), _jsonSerializer.Serialize(data, true));
        }
    }
}
