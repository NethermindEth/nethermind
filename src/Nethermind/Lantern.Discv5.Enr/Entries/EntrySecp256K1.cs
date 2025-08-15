using System.Text;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr.Entries;

public class EntrySecp256K1(byte[] value) : IEntry
{
    public byte[] Value { get; } = value;

    public EnrEntryKey Key => EnrEntryKey.Secp256K1;

    public IEnumerable<byte> EncodeEntry()
    {
        return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(Key, Encoding.ASCII),
            RlpEncoder.EncodeBytes(Value));
    }
}