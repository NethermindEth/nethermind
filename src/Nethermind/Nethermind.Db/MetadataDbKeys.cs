// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db
{
    public static class MetadataDbKeys
    {
        public const int TerminalPoWHash = 1;
        public const int TerminalPoWNumber = 2;
        public const int FinalizedBlockHash = 3;
        public const int SafeBlockHash = 4;
        public const int BeaconSyncPivotHash = 5;
        public const int BeaconSyncPivotNumber = 6;
        public const int LowestInsertedBeaconHeaderHash = 7;
        public const int FirstPoSHash = 8;
    }
}
