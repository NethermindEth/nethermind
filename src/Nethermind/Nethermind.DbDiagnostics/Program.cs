using System;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Config;
using Nethermind.Store;

namespace Nethermind.DbDiagnostics
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");
            DbOnTheRocks db1 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\state", DbConfig.Default);
//            DbOnTheRocks db2 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\code");
//            DbOnTheRocks db3 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\blocks");
//            DbOnTheRocks db4 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\discoveryNodes");
//            DbOnTheRocks db5 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\peers");
//            DbOnTheRocks db6 = new DbOnTheRocks("C:\\chains\\mainnet\\discovery\\receipts");
            byte[] result =  db1.Get(Keccak.Zero);
//            result =  db2.Get(Keccak.Zero);
//            result =  db3.Get(Keccak.Zero);
//            result =  db4.Get(Keccak.Zero);
//            result =  db5.Get(Keccak.Zero);
//            result =  db6.Get(Keccak.Zero);
            Console.WriteLine("and waiting...");
            Console.ReadLine();
        }
    }
}