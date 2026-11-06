#:project ../../src/Nethermind/Nethermind.Consensus/Nethermind.Consensus.csproj
#:property PublishAot=false

using System.Diagnostics;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

if (args.Length is not (7 or 8) || args[2] != "-")
{
    Console.Error.WriteLine("usage: pq-tx <fixtures-bin> <chain-id> - <nonce> <to> <value-wei> <out-prefix> [nonce-key]  (private key hex on stdin)");
    return 2;
}

var fixtures = args[0];
var chainId = ulong.Parse(args[1]);
var key = new PrivateKey(Bytes.FromHexString(Console.In.ReadLine()?.Trim() ?? ""));
var nonce = ulong.Parse(args[3]);
var to = new Address(args[4]);
var value = UInt256.Parse(args[5]);
var outPrefix = args[6];
var nonceKey = args.Length == 8 ? UInt256.Parse(args[7]) : UInt256.Zero;

byte[] Sign(ValueHash256 message)
{
    var output = Path.GetTempFileName();
    using var process = Process.Start(new ProcessStartInfo(fixtures)
    {
        ArgumentList = { "--sign-message", Convert.ToHexString(message.Bytes), output },
        UseShellExecute = false
    })!;
    try
    {
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"fixtures exited {process.ExitCode}");
        return File.ReadAllBytes(output);
    }
    finally
    {
        File.Delete(output);
    }
}

var publicKey = new ValueHash256(Sign(default).AsSpan(0, 32));
var declared = new FrameDependency(Eip8288Constants.LeanSphincsScheme, default, publicKey);
TxFrame[] frames =
[
    new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000UL, nonceKey.IsZero ? 0UL : (ulong)GasCostOf.SSetState, UInt256.Zero, default),
    new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, Eip8288Dependencies.Serialize([declared])),
    new(FrameMode.Sender, FrameFlags.None, to, 50_000, value, default)
];
var tx = new Transaction
{
    Type = TxType.FrameTx,
    ChainId = chainId,
    SenderAddress = key.Address,
    Nonce = nonce,
    NonceKeys = [nonceKey],
    Frames = frames,
    GasLimit = FrameTxValidation.TotalGasLimit(frames),
    GasPrice = 2.GWei,
    DecodedMaxFeePerGas = 100.GWei
};
tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, default)];
var ecdsa = new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(tx));
var raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
raw[0] = ecdsa.RecoveryId;
ecdsa.Bytes.CopyTo(raw.AsSpan(1));
tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, raw)];
tx.Hash = tx.CalculateHash();

var resolved = Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue).Single();
var witness = Sign(resolved.DataHash);

string Encode(byte[] proof) => MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
{
    Transactions = [tx],
    Deps = [resolved],
    Mode = MempoolWrapper.ModeDirect,
    Proofs = [proof]
}).Bytes.ToHexString(true);

var corrupted = (byte[])witness.Clone();
corrupted[^1] ^= 0x01;
File.WriteAllText(outPrefix + ".wrapper.hex", Encode(witness));
File.WriteAllText(outPrefix + ".corrupt.hex", Encode(corrupted));
File.WriteAllText(outPrefix + ".txhash", tx.Hash.ToString());
File.WriteAllText(outPrefix + ".sender", key.Address.ToString());
File.WriteAllText(outPrefix + ".deps", $"data_hash={resolved.DataHash} vk={resolved.VerificationKey} deps_hash={Eip8288Dependencies.ComputeDepsHash([resolved])}");
Console.WriteLine(tx.Hash);
return 0;
