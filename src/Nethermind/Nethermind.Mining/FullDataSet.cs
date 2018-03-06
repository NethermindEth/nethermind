using System;

namespace Nethermind.Mining
{
    public class FullDataSet : IEthashDataSet
    {
        public uint[][] Data { get; set; }
        
        public uint Size => (uint)(Data.Length * Ethash.HashBytes);

        public FullDataSet(ulong setSize, IEthashDataSet cache)
        {
            Console.WriteLine($"building data set of length {setSize}"); // TODO: temp, remove
            Data = new uint[(uint)(setSize / Ethash.HashBytes)][];
            for (uint i = 0; i < Data.Length; i++)
            {
                if (i % 100000 == 0)
                {
                    Console.WriteLine($"building data set of length {setSize}, built {i}"); // TODO: temp, remove
                }

                Data[i] = cache.CalcDataSetItem(i);
            }
        }

        public uint[] CalcDataSetItem(uint i)
        {
            return Data[i];
        }
    }
}