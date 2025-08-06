using System.Text;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr.Entries;

public class UnrecognizedEntry(string key, Rlp.Rlp valueRlp) : IEntry
{
    public string Key { get; } = key;
    public byte[] Value { get; } = valueRlp;

    EnrEntryKey IEntry.Key => new(Key);

    public IEnumerable<byte> EncodeEntry()
    {
        return ByteArrayUtils.JoinByteArrays(RlpEncoder.EncodeString(Key, Encoding.ASCII), valueRlp.Source.ToArray());
    }
}