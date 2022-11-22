// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.BeaconNode.Peering
{
    public static class MethodUtf8
    {
        public static readonly byte[] BeaconBlocksByRange =
            Encoding.UTF8.GetBytes("/eth2/beacon_chain/req/beacon_blocks_by_range/1/");

        public static readonly byte[] Status = Encoding.UTF8.GetBytes("/eth2/beacon_chain/req/status/1/");

        public static readonly byte[] StatusMothraAlternative = Encoding.UTF8.GetBytes("HELLO");
    }
}
