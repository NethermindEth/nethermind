namespace Lantern.Discv5.Enr;

public interface IEnrEntryRegistry
{
    void RegisterEntry(string key, Func<byte[], IEntry> entryCreator);

    void UnregisterEntry(string key);

    IEntry? GetEnrEntry(string stringKey, Rlp.Rlp value);
}