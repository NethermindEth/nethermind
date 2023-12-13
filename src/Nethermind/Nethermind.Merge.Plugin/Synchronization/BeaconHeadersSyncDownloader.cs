// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Synchronization.FastBlocks;

namespace Nethermind.Merge.Plugin.Synchronization;

public class BeaconHeadersSyncDownloader : HeadersSyncDownloader
{
    public BeaconHeadersSyncDownloader(ILogManager logManager) : base(logManager)
    {
    }
}
