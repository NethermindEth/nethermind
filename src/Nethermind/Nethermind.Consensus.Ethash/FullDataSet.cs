// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Ethash
{
    internal class FullDataSet : IEthashDataSet
    {
        private uint[][] Data { get; set; }

        public uint Size => (uint)(Data.Length * Ethash.HashBytes);

        public FullDataSet(ulong setSize, IEthashDataSet cache)
        {
            Data = new uint[(uint)(setSize / Ethash.HashBytes)][];
            for (uint i = 0; i < Data.Length; i++)
            {
                Data[i] = cache.CalcDataSetItem(i);
            }
        }

        public uint[] CalcDataSetItem(uint i)
        {
            return Data[i];
        }

        public void Dispose()
        {
        }
    }
}
