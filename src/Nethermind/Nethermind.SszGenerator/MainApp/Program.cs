using Nethermind.Core.Extensions;
using Program.Generated;
using static System.Console;


namespace Program
{
    //[Class]
    public partial class MainApp(){
        //[Field]
        public static string Name = "Elon";

        //[Function]
        public static void Main()
        {
            WriteLine("Hello, World!");
            var mainApp = new TestAb();
            WriteLine($"Container: {new TestAbSszSerializer().Serialize(ref mainApp).ToHexString()}");
        }
    }
}


