using System.Reflection;
using static System.Console;
using Nethermind.Serialization.Ssz;
using System.Text;
using System;

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

            Product.ProductInfo prodInfo = new Product.ProductInfo
            {
                ProductName = "AA",
                ProductPrice = 40
            };
            
            WriteLine(string.Join(" ",ProductGenerated.GenerateStart("1",prod)));
            // WriteLine($"total byte size is: {CalculateByteSize(prodInfo)}");
        }

    // public static int CalculateByteSize(object obj)
    // {
    //     if (obj == null) return 0;
    //     Type type = obj.GetType();
    //     int totalSize = 0;

    //     //since string is a special type, it will not go through checks like other types will
    //     if(type == typeof(string))
    //         return Encoding.UTF8.GetByteCount((string)obj);
        
    //     foreach (var field in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
    //     {
    //         if(field!=null){
    //             object? fieldValue = field.GetValue(obj);
    //             totalSize += CalculateFieldSize(field.FieldType, fieldValue??0);
    //             WriteLine("totalSize, "+ totalSize);
    //         }
    //     }

    //     // foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
    //     // {
    //     //     WriteLine("property: "+property);
    //     //     if (property != null)
    //     //     {
    //     //         Console.WriteLine("In property: " + property.Name);
    //     //         object? propertyValue = property.GetValue(obj);
    //     //         WriteLine("here ");
    //     //         totalSize += CalculateFieldSize(property.PropertyType, propertyValue ?? 0);
    //     //     }
    //     // }

    //     return totalSize;
    // }


    // //calculates primitive types, arrays and enums
    // private static int CalculateFieldSize(Type fieldType, object fieldValue)
    // {
    //     if (fieldValue == null) return 0;

    //     if (fieldType == typeof(byte) || fieldType == typeof(sbyte))
    //         return sizeof(byte);

    //     if (fieldType == typeof(bool))
    //         return sizeof(bool);

    //     if (fieldType == typeof(short) || fieldType == typeof(ushort))
    //         return sizeof(short);

    //     if (fieldType == typeof(int) || fieldType == typeof(uint))
    //         return sizeof(int);

    //     if (fieldType == typeof(long) || fieldType == typeof(ulong))
    //         return sizeof(long);

    //     if (fieldType == typeof(float))
    //         return sizeof(float);

    //     if (fieldType == typeof(double))
    //         return sizeof(double);

    //     if (fieldType == typeof(char))
    //         return sizeof(char);

    //     if (fieldType == typeof(decimal))
    //         return sizeof(decimal);

    //     if (fieldType == typeof(string))
    //         return Encoding.UTF8.GetByteCount((string)fieldValue);

    //     if (fieldType.IsArray)
    //     {
    //         Array array = (Array)fieldValue;
    //         int arraySize = 0;
    //         foreach (var element in array)
    //         {
    //             arraySize += CalculateFieldSize(element.GetType(), element);
    //         }
    //         return arraySize;
    //     }

    //     if (typeof(System.Collections.IEnumerable).IsAssignableFrom(fieldType))
    //     {
    //         int enumerableSize = 0;
    //         foreach (var element in (System.Collections.IEnumerable)fieldValue)
    //         {
    //             enumerableSize += CalculateFieldSize(element.GetType(), element);
    //         }
    //         return enumerableSize;
    //     }

    //     /***
    //     If type is custom defined struct / type, calls CalculateByteSize()
    //     This creates a recursive function calling
    //     */
    //     return CalculateByteSize(fieldValue);
    // }

    }
}