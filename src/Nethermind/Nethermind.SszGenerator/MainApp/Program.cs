using Nethermind.Core.Extensions;
using Program.Generated;
using static System.Console;
using Nethermind.Serialization.Ssz;
using System.Text;
using System;
using SSZAttribute;

namespace Program{
    [SSZClass]
    public partial class MainApp(){
        [SSZField]
        public static string Name = "HelloWorld";
        [SSZField]
        public static int Age = 32;

        [SSZStruct]
        public struct SequencedTransaction
        {
            public int Index{get;set;}
            public ulong Eon {get;set;}
            // public byte[] EncryptedTransaction {get;set;}
            public ulong GasLimit {get;set;}

            public G1 g1{get;set;}
            public byte[] IdentityPreimage {get;set;}
        }

        [SSZStruct]
        public struct G1
        {
            public byte[] Name {get;set;}
            public int Age {get;set;}
        }

        [SSZFunction]
        public static void Main()
        {
            WriteLine("Hello, World!");
            
            MainAppGenerated.G1 g1 = new MainAppGenerated.G1
            {
                Name = new byte[] { 0x0A, 0x0B, 0x0C },
                Age = 40
            };

            MainAppGenerated.SequencedTransaction sequencedTransaction = new MainAppGenerated.SequencedTransaction
            {
                Index = 1,
                Eon = 12345,
                GasLimit = 67890,
                g1 = g1,
                IdentityPreimage = new byte[] { 0x07, 0x08, 0x09 }
            };

            WriteLine(string.Join(" ",MainAppGenerated.GenerateStart("nameIsName",23,sequencedTransaction,g1)));
            
            ProductGenerated.ProductInfo prod = new ProductGenerated.ProductInfo
            {
                ProductName = "AA",
                ProductPrice = 40
            };
            
            WriteLine(string.Join(" ",ProductGenerated.GenerateStart(0,prod)));
        }
    }
}


