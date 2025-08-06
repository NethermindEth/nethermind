using System.Text;
using Lantern.Discv5.Enr.Identity;
using Lantern.Discv5.Rlp;

namespace Lantern.Discv5.Enr;

public sealed class EnrFactory(IEnrEntryRegistry entryRegistry) : IEnrFactory
{
    public Enr CreateFromString(string enrString, IIdentityVerifier verifier)
    {
        if (string.IsNullOrEmpty(enrString))
            throw new ArgumentNullException(nameof(enrString));

        if (enrString.StartsWith("enr:"))
            enrString = enrString[EnrConstants.EnrPrefixLength..];

        return CreateFromBytes(Base64Url.ToBytes(enrString), verifier);
    }

    public Enr CreateFromBytes(byte[] bytes, IIdentityVerifier verifier)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));

        return CreateFromRlp(RlpDecoder.Decode(bytes)[0], verifier);
    }

    public Enr[] CreateFromMultipleEnrList(ReadOnlySpan<Rlp.Rlp> enrs, IIdentityVerifier verifier)
    {
        Enr[] result = new Enr[enrs.Length];

        for (int i = 0; i < enrs.Length; i++)
        {
            result[i] = CreateFromRlp(enrs[i], verifier);
        }

        return result;
    }

    public Enr CreateFromRlp(Rlp.Rlp enrRlp, IIdentityVerifier verifier)
    {
        var items = RlpDecoder.Decode(enrRlp);
        var signature = items[0];
        var entries = new Dictionary<string, IEntry>();

        for (var i = 2; i < items.Length - 1; i += 2)
        {
            var key = Encoding.ASCII.GetString(items[i]);
            var entry = entryRegistry.GetEnrEntry(key, items[i + 1]);

            if (entry == null)
                continue;

            entries.Add(key, entry);
        }

        var enrRecord = new Enr(entries, verifier, null, signature, RlpExtensions.ByteArrayToUInt64(items[1]));

        return enrRecord;
    }
}
