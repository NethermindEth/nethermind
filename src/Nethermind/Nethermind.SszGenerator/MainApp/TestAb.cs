using Nethermind.Generated.Ssz;


namespace Program
{
    [SszSerializable]
    public struct TestAb {
        public byte[] Bytes { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int? Z { get; set; }
    }
}


