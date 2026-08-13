// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync
{
    [Parallelizable(ParallelScope.All)]
    [TestFixture]
    public class SyncModeExtensionsTests
    {
        [TestCase(SyncMode.None, "None")]
        [TestCase(SyncMode.FastSync | SyncMode.StateNodes | SyncMode.FastHeaders, "FastHeaders, StateNodes, FastSync")]
        [TestCase(SyncMode.FastBodies | SyncMode.FastBlockAccessLists | SyncMode.Full, "FastBlockAccessLists, FastBodies, Full")]
        [TestCase(SyncMode.BeaconHeaders | SyncMode.FastBlockAccessLists | SyncMode.FastBodies, "BeaconHeaders, FastBlockAccessLists, FastBodies")]
        [TestCase(SyncMode.StateNodes | (SyncMode)64, "StateNodes, unknown: 64")]
        [TestCase((SyncMode)64, "unknown: 64")]
        public void Formats_flags_by_name_without_falling_back_to_a_number(SyncMode syncMode, string expected) =>
            Assert.That(syncMode.ToFlagsString(), Is.EqualTo(expected));
    }
}
