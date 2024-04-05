// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization
{
    public class No : IBeaconSyncStrategy
    {
        private No() { }

        public static No BeaconSync { get; } = new();

        public bool ShouldBeInBeaconHeaders() => false;

        public bool ShouldBeInBeaconModeControl() => false;

        public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => true;
        public bool MergeTransitionFinished => false;
        public long? GetTargetBlockHeight() => null;
        public Hash256? GetFinalizedHash() => null;
    }

    public interface IBeaconSyncStrategy
    {
        bool ShouldBeInBeaconHeaders();
        bool ShouldBeInBeaconModeControl();
        bool IsBeaconSyncFinished(BlockHeader? blockHeader);

        public bool MergeTransitionFinished { get; }

        public long? GetTargetBlockHeight();
        public Hash256? GetFinalizedHash();
    }
}
