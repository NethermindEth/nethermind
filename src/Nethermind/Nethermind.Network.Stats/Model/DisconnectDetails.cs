// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Stats.Model
{
    public class DisconnectDetails
    {
        public DisconnectType DisconnectType { get; set; }
        public EthDisconnectReason EthDisconnectReason { get; set; }
    }
}
