// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.P2P.Subprotocols.Les
{
    public class HelperTrieRequest
    {
        public HelperTrieType SubType;
        public long SectionIndex;
        public byte[] Key;
        public long FromLevel;
        public int AuxiliaryData;

        public HelperTrieRequest()
        {
        }

        public HelperTrieRequest(HelperTrieType subType, long sectionIndex, byte[] key, long fromLevel, int auxiliaryData)
        {
            SubType = subType;
            SectionIndex = sectionIndex;
            Key = key;
            FromLevel = fromLevel;
            AuxiliaryData = auxiliaryData;
        }
    }
}
