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
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using NUnit.Framework;

namespace Nethermind.Runner.Test
{
    #pragma warning disable 0618
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class SyncModeCompatibilityTests
    {
        [Test]
        public void FullSync_ByDefault()
        {
            SyncConfig config = new();
            config.SyncMode.Should().Be(StateSyncMode.FullSync);
        }

        [Test]
        public void FastSync_SetInOldWay()
        {
            SyncConfig config = new();
            config.FastSync = true;
            config.SyncMode.Should().Be(StateSyncMode.FastSync);
        }

        [Test]
        public void SnapSync_SetInOldWay()
        {
            SyncConfig config = new();
            config.SnapSync = true;
            config.SyncMode.Should().Be(StateSyncMode.SnapSync);
            config.SyncMode.HasFlag(StateSyncMode.FastSync).Should().Be(true);
        }

        [Test]
        public void OldSettings_AreIgnored_IfSyncModeSet()
        {
            foreach(bool? fastSync in new bool?[]{ false, true, null })
            foreach(bool? snapSync in new bool?[]{ false, true, null })
            foreach (StateSyncMode syncMode in Enum.GetValues(typeof(StateSyncMode)))
            {
                SyncConfig config = new();
                config.SyncMode = syncMode;
                if (fastSync.HasValue)
                {
                    config.FastSync = fastSync.Value;
                }
                if (snapSync.HasValue)
                {
                    config.FastSync = snapSync.Value;
                }

                config.SyncMode.Should().Be(syncMode);
            }
        }
    }
    #pragma warning restore 0618
}
