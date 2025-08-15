using Lantern.Discv5.Enr.Identity;

namespace Lantern.Discv5.Enr;

public interface IEnrFactory
{
    Enr CreateFromString(string enrString, IIdentityVerifier verifier);

    Enr CreateFromBytes(byte[] bytes, IIdentityVerifier verifier);

    Enr[] CreateFromMultipleEnrList(ReadOnlySpan<Rlp.Rlp> enrs, IIdentityVerifier verifier);

    Enr CreateFromRlp(Rlp.Rlp enrRlp, IIdentityVerifier verifier);
}