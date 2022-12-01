// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.BeaconNode.Peering
{
    public static class TopicUtf8
    {
        public static readonly byte[] BeaconBlock = Encoding.UTF8.GetBytes("/eth2/beacon_block/ssz");
    }
}
