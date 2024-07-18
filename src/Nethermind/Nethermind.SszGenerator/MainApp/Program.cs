using System.Reflection;
using static System.Console;
using Nethermind.Serialization.Ssz;


namespace Program{
    [Class]
    public partial class MainApp(){
        [Field]
        public static string Name = "Elon";

        [Function]
        public static void Main()
        {
            WriteLine("Hello, World!");
            var mainApp = new MainApp();

            WriteLine($"Class Name: {mainApp.GetClassName()}");

            WriteLine($"buffer: {mainApp.GenerateBasicType()}");
        }
    }
}