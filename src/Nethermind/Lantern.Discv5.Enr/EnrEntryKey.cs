namespace Lantern.Discv5.Enr;

public record EnrEntryKey(string Value)
{
    public static EnrEntryKey Attnets { get; } = new("attnets");
    public static EnrEntryKey Eth2 { get; } = new("eth2");
    public static EnrEntryKey Id { get; } = new("id");
    public static EnrEntryKey Syncnets { get; } = new("syncnets");
    public static EnrEntryKey Ip { get; } = new("ip");
    public static EnrEntryKey Ip6 { get; } = new("ip6");
    public static EnrEntryKey Secp256K1 { get; } = new("secp256k1");
    public static EnrEntryKey Tcp { get; } = new("tcp");
    public static EnrEntryKey Tcp6 { get; } = new("tcp6");
    public static EnrEntryKey Udp { get; } = new("udp");
    public static EnrEntryKey Udp6 { get; } = new("udp6");

    public static implicit operator string(EnrEntryKey key) => key.Value;
    public static implicit operator EnrEntryKey(string key) => new(key);
}