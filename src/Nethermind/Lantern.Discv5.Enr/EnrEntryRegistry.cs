using System.Net;
using System.Text;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr;

public sealed class EnrEntryRegistry : IEnrEntryRegistry
{
    private readonly Dictionary<EnrEntryKey, Func<byte[], IEntry>> _registeredEntries = new();

    public EnrEntryRegistry()
    {
        RegisterDefaultEntries();
    }

    public EnrEntryRegistry(IEnumerable<(EnrEntryKey, Func<byte[], IEntry>)> entries)
    {
        foreach (var entry in entries)
        {
            _registeredEntries.TryAdd(entry.Item1, entry.Item2);
        }
    }

    public void RegisterEntry(string key, Func<byte[], IEntry> entryCreator)
    {
        _registeredEntries.TryAdd(key, entryCreator);
    }

    public void UnregisterEntry(string key)
    {
        _registeredEntries.Remove(key);
    }

    public IEntry? GetEnrEntry(string stringKey, Rlp.Rlp value)
    {
        return _registeredEntries.TryGetValue(stringKey, out var createEntryFunc) ? createEntryFunc(value) : new UnrecognizedEntry(stringKey, value);
    }

    private void RegisterDefaultEntries()
    {
        var defaultEntries = new List<(EnrEntryKey, Func<byte[], IEntry>)>
        {
            (EnrEntryKey.Attnets, value => new EntryAttnets(value)),
            (EnrEntryKey.Eth2, value => new EntryEth2(value)),
            (EnrEntryKey.Syncnets, value => new EntrySyncnets(value)),
            (EnrEntryKey.Id, value => new EntryId(Encoding.ASCII.GetString(value))),
            (EnrEntryKey.Ip, value => new EntryIp(new IPAddress(value))),
            (EnrEntryKey.Ip6, value => new EntryIp6(new IPAddress(value))),
            (EnrEntryKey.Secp256K1, value => new EntrySecp256K1(value)),
            (EnrEntryKey.Tcp, value => new EntryTcp(RlpExtensions.ByteArrayToInt32(value))),
            (EnrEntryKey.Tcp6, value => new EntryTcp6(RlpExtensions.ByteArrayToInt32(value))),
            (EnrEntryKey.Udp, value => new EntryUdp(RlpExtensions.ByteArrayToInt32(value))),
            (EnrEntryKey.Udp6, value => new EntryUdp6(RlpExtensions.ByteArrayToInt32(value)))
        };

        foreach (var entry in defaultEntries)
        {
            RegisterEntry(entry.Item1, entry.Item2);
        }
    }
}