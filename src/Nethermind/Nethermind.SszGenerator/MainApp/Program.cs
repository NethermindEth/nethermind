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

            byte[] buffer = new byte[100];
            Span<byte> span = buffer;

            byte valueByte=4;

            byte[] value1 = {1,2,3,4};
            byte[] value2 = {5,6,7,8};
            // int offset=0;

            Ssz.Encode(span,valueByte);
            Ssz.Encode(span,valueByte);

            WriteLine(string.Join(", ",buffer));
        }
    }
}