namespace Nethermind.Experimental.Abi.V1;

public interface IAbi<T>
{
    public static abstract string Display { get; }
    public static abstract T Read(BinaryReader r);
    public static abstract void Write(BinaryWriter w, T v);
}

public sealed class AbiString : IAbi<string>
{
    public static string Display => "string";

    public static string Read(BinaryReader r) => r.ReadString();

    public static void Write(BinaryWriter w, string v) => w.Write(v);
}

public sealed class AbiInt32 : IAbi<Int32>
{
    public static string Display => "int32";

    public static Int32 Read(BinaryReader r) => r.ReadInt32();

    public static void Write(BinaryWriter w, Int32 v) => w.Write(v);
}

public sealed record AbiSignature(string Name)
{
    public AbiSignature<T1, V1> Arg<T1, V1>() where T1 : IAbi<V1> => new(Name);

    public override string ToString() => $"{Name}()";
}

public sealed record AbiSignature<T1, V1>(string Name)
    where T1 : IAbi<V1>
{
    public AbiSignature<T1, V1, T2, V2> Arg<T2, V2>() where T2 : IAbi<V2> => new(Name);

    public override string ToString() => $"{Name}({T1.Display})";
}

public sealed record AbiSignature<T1, V1, T2, V2>(string Name)
    where T1 : IAbi<V1> where T2 : IAbi<V2>
{
    public override string ToString() => $"{Name}({T1.Display}, {T2.Display})";
}

public sealed class AbiEncoder
{
    public byte[] Encode<T1, V1, T2, V2>(AbiSignature<T1, V1, T2, V2> _, V1 arg1, V2 arg2)
        where T1 : IAbi<V1> where T2 : IAbi<V2>
    {
        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);

        T1.Write(w, arg1);
        T2.Write(w, arg2);

        return buffer.ToArray();
    }

    public (V1, V2) Decode<T1, V1, T2, V2>(AbiSignature<T1, V1, T2, V2> _, byte[] source)
        where T1 : IAbi<V1> where T2 : IAbi<V2>
    {
        using var reader = new BinaryReader(new MemoryStream(source));

        V1 v1 = T1.Read(reader);
        V2 v2 = T2.Read(reader);

        return (v1, v2);
    }
};

public static class Program
{
    public static void Main()
    {
        var signature = new AbiSignature("example")
            .Arg<AbiString, string>()
            .Arg<AbiInt32, Int32>();

        Console.WriteLine(signature.ToString());

        var encoder = new AbiEncoder();
        byte[] encoded = encoder.Encode(signature, "hello", 44);
        (string a, Int32 b) = encoder.Decode(signature, encoded);

        Console.WriteLine($"Decoded a = {a}");
        Console.WriteLine($"Decoded b = {b}");
    }
}
