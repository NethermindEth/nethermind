using Nethermind.Core.Extensions;
using Nethermind.Generated.Ssz;
using Program.Generated;
using System.Reflection;
using static System.Console;


namespace Program{
    //[Class]
    public partial class MainApp(){
        //[Field]
        public static string Name = "Elon";

        //[Function]
        public static void Main()
        {
            WriteLine("Hello, World!");
            var mainApp = new TestAB();
            WriteLine($"Container: {new TestABSszSerializer().Serialize(ref mainApp).ToHexString()}");
        }
    }

    [SszSerializable]
    public struct TestAB {
        public byte[] Bytes { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int? Z { get; set; }
    }
}


