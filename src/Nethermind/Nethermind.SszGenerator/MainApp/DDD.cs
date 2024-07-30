//using Nethermind.Serialization.Ssz;

//namespace Program.Generated;

//public partial class TestABSszSerializer
//{
//    public int GetLength(ref TestAB container)
//    {
//        return 4 + container.Bytes.Length +
//               4 +
//               4 +
//               (container.Z is null ? 4 : 8);
//    }

//    public ReadOnlySpan<byte> Serialize(ref TestAB container)
//    {
//        Span<byte> buf = new byte[GetLength(ref container)];

//        int dynOffset1 = 16;
//        int dynOffset2 = dynOffset1 + container.Bytes.Length;

//        Ssz.Encode(buf.Slice(0, 4), dynOffset1);
//        Ssz.Encode(buf.Slice(4, 8), container.X);
//        Ssz.Encode(buf.Slice(8, 12), container.Y);
//        Ssz.Encode(buf.Slice(12, 16), dynOffset2);

//        Ssz.Encode(buf.Slice(dynOffset1, dynOffset1), container.Bytes);
//        if (container.Z is not null) Ssz.Encode(buf.Slice(dynOffset2, dynOffset2), container.Z.Value);

//        return buf;
//    }
//}
