using System.Reflection;
using static System.Console;
using Nethermind.Serialization.Ssz;

namespace Program{
    [SSZClass]
    public partial class Product(){

        [SSZField]
        public int ProductId = 0;

        [SSZStruct]
        public struct ProductInfo
        {
            public string ProductName {get;set;}
            public ulong ProductPrice {get;set;}
        }

        [SSZFunction]
        public static void Gen()
        {
        }

    }
}