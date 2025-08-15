using Epoche;
using Lantern.Discv5.Enr.Entries;
using Lantern.Discv5.Rlp;
using NBitcoin.Secp256k1;

namespace Lantern.Discv5.Enr.Identity.V4;

public class IdentityVerifierV4 : IIdentityVerifier
{
    public bool VerifyRecord(IEnr record)
    {
        var publicKeyBytes = record.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
        var publicKey = Context.Instance.CreatePubKey(publicKeyBytes);
        SecpECDSASignature.TryCreateFromCompact(record.Signature, out var signature);

        if (signature == null) throw new InvalidOperationException("Failed to verify ENR record.");

        return publicKey.SigVerify(signature, Keccak256.ComputeHash(record.EncodeContent()));
    }

    public byte[] GetNodeIdFromRecord(IEnr record)
    {
        var publicKeyBytes = record.GetEntry<EntrySecp256K1>(EnrEntryKey.Secp256K1).Value;
        var publicKey = Context.Instance.CreatePubKey(publicKeyBytes);
        var xBytes = publicKey.Q.x.ToBytes();
        var yBytes = publicKey.Q.y.ToBytes();
        var publicKeyUncompressed = ByteArrayUtils.JoinByteArrays(xBytes, yBytes);
        return Keccak256.ComputeHash(publicKeyUncompressed);
    }
}