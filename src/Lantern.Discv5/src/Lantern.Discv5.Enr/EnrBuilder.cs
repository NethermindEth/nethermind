using Lantern.Discv5.Enr.Identity;

namespace Lantern.Discv5.Enr;

public class EnrBuilder
{
    private readonly Dictionary<string, IEntry> _entries = [];
    private IIdentityVerifier? _verifier;
    private IIdentitySigner? _signer;

    public EnrBuilder WithIdentityScheme(IIdentityVerifier verifier, IIdentitySigner signer)
    {
        _verifier = verifier;
        _signer = signer;
        return this;
    }

    public EnrBuilder WithEntry(string key, IEntry? entry)
    {
        if (entry != null)
            _entries[key] = entry;

        return this;
    }

    public Enr Build()
    {
        if (_verifier == null)
        {
            throw new InvalidOperationException("Verifier must be set before building the EnrRecord.");
        }

        var enrRecord = new Enr(_entries, _verifier, _signer);

        enrRecord.UpdateSignature();

        return enrRecord;
    }
}
