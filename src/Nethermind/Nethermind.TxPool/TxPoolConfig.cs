// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.TxPool
{
    public class TxPoolConfig : ITxPoolConfig
    {
        public int PeerNotificationThreshold { get; set; } = 5;
        public int Size { get; set; } = 2048;
        public int HashCacheSize { get; set; } = 512 * 1024;
        public long? GasLimit { get; set; } = null;
        public int? ReportMinutes { get; set; } = null;
    }
}
