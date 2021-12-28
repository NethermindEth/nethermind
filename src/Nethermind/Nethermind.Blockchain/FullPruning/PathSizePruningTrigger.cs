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
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using Nethermind.Core.Timers;

namespace Nethermind.Blockchain.FullPruning
{
    /// <summary>
    /// Allows to trigger full pruning based on size of the path (by default state database). 
    /// </summary>
    /// <remarks>
    /// It checks the size of the path every 5 minutes.
    /// </remarks>
    public class PathSizePruningTrigger : IPruningTrigger, IDisposable
    {
        private readonly string _path;
        private readonly long _threshold;
        private readonly IFileSystem _fileSystem;
        private readonly ITimer _timer;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="path">The path to watch.</param>
        /// <param name="threshold">Threshold in bytes that if exceeded by <see cref="path"/> will trigger full pruning.</param>
        /// <param name="timerFactory">Factory for timers.</param>
        /// <param name="fileSystem">File system access.</param>
        /// <exception cref="ArgumentException">Thrown if <see cref="path"/> doesn't exist.</exception>
        public PathSizePruningTrigger(string path, long threshold, ITimerFactory timerFactory, IFileSystem fileSystem)
        {
            if (!fileSystem.Directory.Exists(path))
            {
                throw new ArgumentException($"{path} is not a directory", nameof(path));
            }
            
            _path = path;
            _threshold = threshold;
            _fileSystem = fileSystem;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMinutes(5));
            _timer.Elapsed += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            long size = GetDbSize();
            if (size >= _threshold)
            {
                Prune?.Invoke(this, new PruningEventArgs());
            }
        }

        private long GetDbSize()
        {
            // we try to check default directory and only if its empty we go to indexed subdirectory.
            long size = GetPathSize(_path);
            if (size == 0)
            {
                size = GetPathSize(GetDbIndex(_path));
            }
            return size;
        }

        /// <summary>
        /// Gets the sub path to current indexed database.
        /// </summary>
        /// <param name="path">Main database path.</param>
        /// <returns>Current indexed database sub path.</returns>
        private string GetDbIndex(string path)
        {
            int? firstIndexSubDirectory = _fileSystem.Directory
                .EnumerateDirectories(path)
                .Select(d => _fileSystem.Path.GetFileName(d))
                .Select(n => int.TryParse(n, out int index) ? (int?)index : null)
                .Where(i => i is not null)
                .OrderBy(i => i)
                .FirstOrDefault();

            return firstIndexSubDirectory is null ? path : _fileSystem.Path.Combine(path, firstIndexSubDirectory.Value.ToString());
        }

        
        /// <summary>
        /// Gets the size of the path.
        /// </summary>
        /// <param name="path">Path</param>
        /// <returns>Size of path</returns>
        /// <remarks>
        /// Enumerate the files in the directory and sums their size.
        /// RocksDB doesn't use subdirectories.
        /// </remarks>
        private long GetPathSize(string path)
        {
            IEnumerable<IFileInfo> files = _fileSystem.DirectoryInfo.FromDirectoryName(path).EnumerateFiles();
            long size = files.Sum(f => f.Length);
            return size;
        }

        /// <inheritdoc />
        public event EventHandler<PruningEventArgs>? Prune;
        
        /// <inheritdoc />
        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
