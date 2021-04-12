using System;
using System.IO;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Tools.GasHistorian
{
    static class Program
    {
        private static readonly ChainLevelDecoder _chainLevelDecoder = new();
        private static readonly BlockDecoder _blockDecoder = new();

        private static long _txCount = 0;
        
        static void Main(string[] args)
        {
            string baseDir = args[0];
            Console.WriteLine($"Scanning blocks in {baseDir}");
            
            RocksDbSettings chainDbSettings = new("blockInfos", "blockInfos");
            DbOnTheRocks chainDb = new(
                baseDir,
                chainDbSettings,
                DbConfig.Default,
                LimboLogs.Instance);
            
            RocksDbSettings blocksDbSettings = new("blocks", "blocks");
            DbOnTheRocks blocksDb = new(
                baseDir,
                blocksDbSettings,
                DbConfig.Default,
                LimboLogs.Instance);

            using FileStream fs = File.OpenWrite("output.csv");
            using StreamWriter sw = new(fs);
            sw.WriteLine($"Block Nr,Block Gas Limit,Tx Index,Tx Gas Limit,Tx Gas Price");
            for (int i = 0; i < 15000000; i++)
            {
                if (i % 10000 == 0)
                {
                    Console.WriteLine($"Scanning block {i}, found {_txCount} txs");
                }
                
                ChainLevelInfo? chainLevelInfo = chainDb.Get(i, _chainLevelDecoder);

                BlockInfo? mainChainBlock = chainLevelInfo?.MainChainBlock;
                if (mainChainBlock is not null)
                {
                    Block? block = blocksDb.Get(mainChainBlock.BlockHash, _blockDecoder);
                    
                    if (block is not null)
                    {
                        sw.WriteLine($"{block.Number},{block.GasLimit},,,");
                        for (int j = 0; j < block.Transactions.Length; j++)
                        {
                            _txCount++;
                            Transaction transaction = block.Transactions[j]; 
                            sw.WriteLine($"{block.Number},{block.GasLimit},{j},{transaction.GasLimit},{transaction.GasPrice}");
                        }
                    }
                }   
            }
        }
    }
}
