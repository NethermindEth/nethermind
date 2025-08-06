using System.Net;
using System.Text;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr.Entries;

public class EntryIp(IPAddress value) : IEntry
{
    public IPAddress Value { get; } = value;

    public EnrEntryKey Key => EnrEntryKey.Ip;

    public IEnumerable<byte> EncodeEntry()
    {
        return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(Key, Encoding.ASCII),
            RlpEncoder.EncodeBytes(Value.GetAddressBytes()));
    }
}