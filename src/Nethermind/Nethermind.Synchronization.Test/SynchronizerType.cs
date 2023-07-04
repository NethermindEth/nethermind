// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.Test
{
    public enum SynchronizerType
    {
        Full,
        Fast,
        Eth2MergeFull,
        Eth2MergeFast,
        Eth2MergeFastWithoutTTD,
        Eth2MergeFullWithoutTTD
    }
}
