// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Snap
{
    public static class SnapMessageCode
    {
        public const int GetAccountRange = 0x00;
        public const int AccountRange = 0x01;
        public const int GetStorageRanges = 0x02;
        public const int StorageRanges = 0x03;
        public const int GetByteCodes = 0x04;
        public const int ByteCodes = 0x05;
        public const int GetTrieNodes = 0x06;
        public const int TrieNodes = 0x07;
    }
}
