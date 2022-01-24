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
using System.IO.Abstractions;
using Nethermind.Core.Timers;

namespace Nethermind.Blockchain.FullPruning
{
    public class DiskFreeSpacePruningTrigger : IPruningTrigger, IDisposable
    {
        private readonly string _path;
        private readonly long _threshold;
        private readonly IFileSystem _fileSystem;
        private readonly ITimer _timer;

        public DiskFreeSpacePruningTrigger(string path, long threshold, ITimerFactory timerFactory, IFileSystem fileSystem)
        {
            _path = path;
            _threshold = threshold;
            _fileSystem = fileSystem;
            _timer = timerFactory.CreateTimer(TimeSpan.FromMinutes(5));
            _timer.Elapsed += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            string driveName = _fileSystem.Path.GetPathRoot(_fileSystem.Path.GetFullPath(_path));
            IDriveInfo drive = _fileSystem.DriveInfo.FromDriveName(driveName);
            if (drive.AvailableFreeSpace < _threshold)
            {
                Prune?.Invoke(this, new PruningTriggerEventArgs());
            }
        }

        public event EventHandler<PruningTriggerEventArgs>? Prune;
        
        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
