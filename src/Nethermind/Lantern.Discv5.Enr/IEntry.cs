namespace Lantern.Discv5.Enr;

public interface IEntry
{
    EnrEntryKey Key { get; }

    IEnumerable<byte> EncodeEntry();
}