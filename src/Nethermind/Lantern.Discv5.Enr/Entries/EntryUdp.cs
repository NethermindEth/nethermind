using System.Text;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr.Entries;

public class EntryUdp(int value) : IEntry
{
    public int Value { get; } = value;

    public EnrEntryKey Key => EnrEntryKey.Udp;

    public IEnumerable<byte> EncodeEntry()
    {
        return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(Key, Encoding.ASCII),
            RlpEncoder.EncodeInteger(Value));
    }
}