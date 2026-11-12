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
// Without a nonce key the transaction uses the account nonce: empty nonce_keys (key 0 is an ordinary keyed nonce).
UInt256[] nonceKeys = args.Length == 8 ? [UInt256.Parse(args[7])] : [];

// PQ_SCHEME: sphincs (default), stark, or mixed (one dependency of each scheme in the same transaction).
var scheme = Environment.GetEnvironmentVariable("PQ_SCHEME") ?? "sphincs";
if (scheme is not ("sphincs" or "stark" or "mixed")) throw new ArgumentException($"unknown PQ_SCHEME {scheme}");

// `key ‖ witness` from the fixtures binary: a NiceTry v2 signature, or a leanSTARK proof of its accept-all program.
byte[] Witness(byte depScheme, ValueHash256 message)
{
    var output = Path.GetTempFileName();
    using var process = Process.Start(new ProcessStartInfo(fixtures)
    {
        ArgumentList = { depScheme == Eip8288Constants.LeanStarkScheme ? "--prove-stark" : "--sign-message", Convert.ToHexString(message.Bytes), output },
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

byte[] schemes = scheme switch
{
    "stark" => [Eip8288Constants.LeanStarkScheme],
    "mixed" => [Eip8288Constants.LeanSphincsScheme, Eip8288Constants.LeanStarkScheme],
    _ => [Eip8288Constants.LeanSphincsScheme],
};
FrameDependency[] declared = [.. schemes.Select(s => new FrameDependency(s, default, new ValueHash256(Witness(s, default).AsSpan(0, 32))))];
TxFrame[] frames =
[
    new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000UL, nonceKeys.Length == 0 ? 0UL : (ulong)GasCostOf.SSetState, UInt256.Zero, default),
    new(FrameMode.DepVerify, FrameFlags.None, null, declared.Aggregate(0UL, static (gas, d) => gas + d.VerificationGas), UInt256.Zero, Eip8288Dependencies.Serialize(declared)),
    new(FrameMode.Sender, FrameFlags.None, to, 50_000, value, default)
];
var gasLimit = FrameTxValidation.TotalGasLimit(frames);
var tx = new Transaction
{
    Type = TxType.FrameTx,
    ChainId = chainId,
    SenderAddress = key.Address,
    Nonce = nonce,
    NonceKeys = nonceKeys,
    Frames = frames,
    GasLimit = gasLimit,
    GasPrice = 2.GWei,
    MaxFee = (UInt256)gasLimit * 100.GWei
};
tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, new byte[TxFrameSignature.Secp256k1SignatureLength])];
var ecdsa = new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(tx));
var raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
raw[0] = ecdsa.RecoveryId;
ecdsa.Bytes.CopyTo(raw.AsSpan(1));
tx.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, raw)];
tx.Hash = tx.CalculateHash();

var resolved = Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue);
// A leanSPHINCS witness is `key ‖ signature`; a leanSTARK witness is the proof alone.
var witnesses = resolved.Select(d => d.Scheme == Eip8288Constants.LeanStarkScheme ? Witness(d.Scheme, d.DataHash)[32..] : Witness(d.Scheme, d.DataHash)).ToArray();

string Encode(byte[][] proofs) => MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
{
    Transactions = [tx],
    Deps = [.. resolved],
    Mode = MempoolWrapper.ModeDirect,
    Proofs = proofs
}).Bytes.ToHexString(true);

// The last dependency's witness is corrupted: the leanSTARK one in a mixed transaction.
var corrupted = witnesses.Select(w => (byte[])w.Clone()).ToArray();
corrupted[^1][^1] ^= 0x01;
File.WriteAllText(outPrefix + ".wrapper.hex", Encode(witnesses));
File.WriteAllText(outPrefix + ".corrupt.hex", Encode(corrupted));
File.WriteAllText(outPrefix + ".txhash", tx.Hash.ToString());
File.WriteAllText(outPrefix + ".sender", key.Address.ToString());
File.WriteAllText(outPrefix + ".deps", string.Join('\n', resolved.Select(d => $"scheme=0x{d.Scheme:x2} data_hash={d.DataHash} vk={d.VerificationKey}")) + $"\ndeps_hash={Eip8288Dependencies.ComputeDepsHash(resolved)}");
Console.WriteLine(tx.Hash);
return 0;
