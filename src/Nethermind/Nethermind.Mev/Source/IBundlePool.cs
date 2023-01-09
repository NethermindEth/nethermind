// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev.Source
{
    public interface IBundlePool : IBundleSource
    {
        event EventHandler<BundleEventArgs> NewReceived;
        event EventHandler<BundleEventArgs> NewPending;
        bool AddBundle(MevBundle bundle);
        bool AddMegabundle(MevMegabundle megabundle);
        IEnumerable<MevBundle> GetBundles(long block, UInt256 timestamp, CancellationToken token = default);
        IEnumerable<MevBundle> GetMegabundles(long block, UInt256 timestamp, CancellationToken token = default);
    }

    public static class BundlePoolExtensions
    {
        public static IEnumerable<MevBundle> GetBundles(this IBundlePool bundleSource, BlockHeader parent, ITimestamper timestamper, CancellationToken token = default) =>
            bundleSource.GetBundles(parent.Number + 1, timestamper.UnixTime.Seconds, token);

        public static IEnumerable<MevBundle> GetMegabundles(this IBundlePool bundleSource, BlockHeader parent, ITimestamper timestamper, CancellationToken token = default) =>
            bundleSource.GetMegabundles(parent.Number + 1, timestamper.UnixTime.Seconds, token);
    }
}
