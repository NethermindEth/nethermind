namespace Lantern.Discv5.Enr;

public interface IEnr
{
    byte[]? Signature { get; }

    ulong SequenceNumber { get; }

    byte[] NodeId { get; }

    string ToPeerId();

    string ToEnode();

    bool HasKey(string key);

    void UpdateEntry<T>(T value) where T : class, IEntry;

    T GetEntry<T>(string key, T defaultValue = default!) where T : IEntry;

    byte[] EncodeRecord();

    byte[] EncodeContent();

    void UpdateSignature();
}