using System.Reflection;
using static System.Console;
using Nethermind.Serialization.Ssz;

namespace Program{
    [Class]
    public partial class MainApp(){
        [Field]
        public static string Name = "HelloWorld";

        [FieldStruct]
        public struct SequencedTransaction
        {
            public int Index{get;set;}
            public ulong Eon {get;set;}
            // public byte[] EncryptedTransaction {get;set;}
            public ulong GasLimit {get;set;}

            public G1 g1{get;set;}
            public byte[] IdentityPreimage {get;set;}
        }

        [FieldStruct]
        public struct G1
        {
            public byte[] Name {get;set;}
            public int Age {get;set;}
        }

        [Function]
        public static void Main()
        {
            WriteLine("Hello, World!");
            var mainApp = new MainApp();
            
            // MainAppGenerated.G1 g1 = new MainAppGenerated.G1
            // {
            //     Name = new byte[] { 0x0A, 0x0B, 0x0C },
            //     Age = 40
            // };

            // MainAppGenerated.SequencedTransaction sequencedTransaction = new MainAppGenerated.SequencedTransaction
            // {
            //     Index = 1,
            //     Eon = 12345,
            //     GasLimit = 67890,
            //     g1 = g1,
            //     IdentityPreimage = new byte[] { 0x07, 0x08, 0x09 }
            // };

            // MainAppGenerated.GenerateStart(sequencedTransaction,g1);

            WriteLine($"Class Name: {mainApp.GetClassName()}");

            byte[] buffer = new byte[100];
            Span<byte> span = buffer;
            int offset = 0;

            Encode(buffer, new SequencedTransaction(), ref offset);

            // Ssz.Encode(span,1);
            // WriteLine(string.Join(", ",buffer));

            // Ssz.Encode(span,2);
            // WriteLine(string.Join(", ",buffer));
        }

        public static void Encode(Span<byte> span, SequencedTransaction sequencedTransaction, ref int offset)
        {
            Ssz.Encode(span, sequencedTransaction.Index, ref offset);
            Ssz.Encode(span, sequencedTransaction.Eon, ref offset);
            // Ssz.Encode(span, sequencedTransaction.G1, ref offset);

            WriteLine(string.Join(" ", span.Slice(0, offset).ToArray()));
        }
    }
}