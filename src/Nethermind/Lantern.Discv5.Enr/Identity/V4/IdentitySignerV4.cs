using Epoche;
using Lantern.Discv5.Rlp;
using NBitcoin.Secp256k1;

namespace Lantern.Discv5.Enr.Identity.V4;

public class IdentitySignerV4(byte[] privateKey) : IIdentitySigner
{
    private readonly ECPrivKey _privateKey = Context.Instance.CreateECPrivKey((ReadOnlySpan<byte>)privateKey);

    public byte[] PublicKey => _privateKey.CreatePubKey().ToBytes();

    public byte[] SignRecord(IEnr record)
    {
        _privateKey.TrySignECDSA(Keccak256.ComputeHash(record.EncodeContent()), out var signature);

        if (signature == null) throw new InvalidOperationException("Failed to sign ENR record.");
        return ByteArrayUtils.JoinByteArrays(signature.r.ToBytes(), signature.s.ToBytes());
    }
}