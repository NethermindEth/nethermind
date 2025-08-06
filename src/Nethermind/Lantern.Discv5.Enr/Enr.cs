using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Rlp;
using Multiformats.Base;
using Multiformats.Hash;

namespace Lantern.Discv5.Enr;

public class Enr : IEnr, IEquatable<IEnr>
{
    private readonly IDictionary<string, IEntry> _entries;
    private readonly IIdentitySigner? _signer;
    private readonly IIdentityVerifier? _verifier;
    private byte[]? _cachedNodeId;

    public Enr(
        IDictionary<string, IEntry> initialEntries,
        IIdentityVerifier verifier,
        IIdentitySigner? signer = null,
        byte[]? signature = null,
        ulong sequenceNumber = 1)
    {
        _entries = initialEntries;
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _signer = signer;

        if (_signer != null)
        {
            Signature = _signer.SignRecord(this);
        }
        else if (signature != null)
        {
            Signature = signature;
        }
        else
        {
            throw new ArgumentNullException($"You must provide either {nameof(signer)} or {nameof(signature)}");
        }

        _ = NodeId;
        SequenceNumber = sequenceNumber;
    }

    public byte[]? Signature { get; private set; }

    public ulong SequenceNumber { get; private set; }

    public byte[] NodeId
    {
        get { return _cachedNodeId ??= _verifier!.GetNodeIdFromRecord(this); }
    }

    public T GetEntry<T>(string key, T defaultValue = default!) where T : IEntry
    {
        var entry = _entries.Values.FirstOrDefault(e => e.Key == key);

        return entry is T result ? result : defaultValue;
    }

    public void UpdateEntry<T>(T value) where T : class, IEntry
    {
        foreach (var existingKey in _entries.Where(entry => entry.Value.Key.Equals(value.Key)).ToList())
        {
            _entries.Remove(existingKey.Key);
        }

        _entries[value.Key] = value;
        IncrementSequenceNumber();
    }

    public bool HasKey(string key)
    {
        return _entries.ContainsKey(key);
    }

    public void UpdateSignature()
    {
        if (_signer != null)
            Signature = _signer.SignRecord(this);
    }

    public byte[] EncodeContent()
    {
        var encodedContent = EncodeEnrContent();
        var encodedSeq = RlpEncoder.EncodeUlong(SequenceNumber);
        var encodedItems = ByteArrayUtils.Concatenate(encodedSeq, encodedContent);
        return RlpEncoder.EncodeCollectionOfBytes(encodedItems);
    }

    public byte[] EncodeRecord()
    {
        if (Signature == null)
            throw new InvalidOperationException("Signature must be set before encoding.");

        var encodedSignature = RlpEncoder.EncodeBytes(Signature);
        var encodedSeq = RlpEncoder.EncodeUlong(SequenceNumber);
        var encodedContent = EncodeEnrContent();
        var encodedItems = ByteArrayUtils.Concatenate(encodedSignature, encodedSeq, encodedContent);
        return RlpEncoder.EncodeCollectionOfBytes(encodedItems);
    }

    public bool Equals(IEnr? other)
    {
        return other != null && NodeId.AsSpan().SequenceEqual(other.NodeId.AsSpan());
    }

    public override string ToString()
    {
        return $"enr:{Base64Url.ToString(EncodeRecord())}";
    }

    public string ToEnode()
    {
        if (Signature == null)
            throw new InvalidOperationException("Signature must be set before encoding.");

        if (!HasKey(EnrEntryKey.Tcp))
            return $"enode://{Convert.ToHexString(Signature).ToLower()}@{GetEntry<EntryIp>(EnrEntryKey.Ip).Value}?discport={GetEntry<EntryUdp>(EnrEntryKey.Udp).Value}";

        return $"enode://{Convert.ToHexString(Signature).ToLower()}@{GetEntry<EntryIp>(EnrEntryKey.Ip).Value}:{GetEntry<EntryTcp>(EnrEntryKey.Tcp).Value}?discport={GetEntry<EntryUdp>(EnrEntryKey.Udp).Value}";
    }

    public string ToPeerId()
    {
        var publicKey = GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
        var publicKeyProto = ByteArrayUtils.Concatenate(EnrConstants.ProtoBufferPrefix, publicKey);
        var multiHash = publicKeyProto.Length <= 42 ? Multihash.Encode(publicKeyProto, HashType.ID) : Multihash.Encode(publicKeyProto, HashType.SHA2_256);

        return Multibase.Encode(MultibaseEncoding.Base58Btc, multiHash).Remove(0, 1);
    }

    private void IncrementSequenceNumber()
    {
        SequenceNumber++;

        if (_signer != null)
            UpdateSignature();
    }

    private byte[] EncodeEnrContent()
    {
        return _entries
            .SelectMany(e => e.Value.EncodeEntry())
            .ToArray();
    }
}