using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;

namespace Nethermind.Verkle.Proofs;

public class Transcript
{
    private List<byte> _currentHash;


    // we can use a simple formula to calculate the maximum size.
    // 189 - this should be the minimum size
    // 99 for each query
    // 10 for multiproof label
    // 6 for verkle label
    // size = max(189, 10 + 6 + 99 * numOfQueries)
    public Transcript(IEnumerable<byte> label, int size)
    {
        _currentHash = new List<byte>(size);
        _currentHash.AddRange(label);

    }

    public Transcript(string label, int size)
    {
        _currentHash = new List<byte>(size);
        _currentHash.AddRange(Encoding.ASCII.GetBytes(label));
    }

    public Transcript(IEnumerable<byte> label)
    {
        _currentHash = new(792000);
        _currentHash.AddRange(label);

    }

    public Transcript(string label)
    {
        _currentHash = new(792000);
        _currentHash.AddRange(Encoding.ASCII.GetBytes(label));
    }

    private void AppendBytes(IEnumerable<byte> message, IEnumerable<byte> label)
    {
        _currentHash.AddRange(label);
        _currentHash.AddRange(message);
    }

    public void AppendScalar(FrE scalar, IEnumerable<byte> label)
    {
        AppendBytes(scalar.ToBytes(), label);
    }

    private void AppendScalar(byte scalar, IEnumerable<byte> label)
    {
        byte[] bytes = new byte[32];
        bytes[0] = scalar;
        AppendBytes(bytes, label);
    }

    public void AppendScalar(FrE scalar, string label)
    {
        AppendScalar(scalar, Encoding.ASCII.GetBytes(label));
    }

    public void AppendScalar(byte scalar, string label)
    {
        AppendScalar(scalar, Encoding.ASCII.GetBytes(label));
    }

    public void AppendPoint(Banderwagon point, byte[] label)
    {
        AppendBytes(point.ToBytes(), label);
    }

    public void AppendPoint(AffinePoint normalizedPoint, byte[] label)
    {
        AppendBytes(Banderwagon.ToBytes(in normalizedPoint), label);
    }

    public void AppendPoint(Banderwagon point, string label)
    {
        AppendPoint(point, Encoding.ASCII.GetBytes(label));
    }

    public void AppendPoint(AffinePoint normalizedPoint, string label)
    {
        AppendBytes(Banderwagon.ToBytes(in normalizedPoint), Encoding.ASCII.GetBytes(label));
    }

    public FrE ChallengeScalar(byte[] label)
    {
        DomainSep(label);
        byte[] hash = SHA256.HashData(CollectionsMarshal.AsSpan(_currentHash));
        FrE challenge = FrE.FromBytesReduced(hash);
        _currentHash = [];

        AppendScalar(challenge, label);
        return challenge;
    }

    public FrE ChallengeScalar(string label)
    {
        return ChallengeScalar(Encoding.ASCII.GetBytes(label));
    }

    public void DomainSep(byte[] label)
    {
        _currentHash.AddRange(label);
    }

    public void DomainSep(string label)
    {
        DomainSep(Encoding.ASCII.GetBytes(label));
    }
}
