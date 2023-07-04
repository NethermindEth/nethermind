// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO.Abstractions;
using Nethermind.Core.Timers;

namespace Nethermind.Blockchain.FullPruning
{
    public class DiskFreeSpacePruningTrigger : IPruningTrigger, IDisposable
    {
        private static readonly TimeSpan _defaultCheckInterval = TimeSpan.FromMinutes(5);
        private readonly string _path;
        private readonly long _threshold;
        private readonly IFileSystem _fileSystem;
        private readonly ITimer _timer;

        public DiskFreeSpacePruningTrigger(string path, long threshold, ITimerFactory timerFactory, IFileSystem fileSystem)
        {
            _path = path;
            _threshold = threshold;
            _fileSystem = fileSystem;
            _timer = timerFactory.CreateTimer(_defaultCheckInterval);
            _timer.Elapsed += OnTick;
            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            string driveName = _fileSystem.Path.GetPathRoot(_fileSystem.Path.GetFullPath(_path));
            IDriveInfo drive = _fileSystem.DriveInfo.New(driveName);
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
