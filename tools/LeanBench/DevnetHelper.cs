// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private static void GenerateDevnetTransactions(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string output = Path.GetFullPath(Value("out", "/tmp/lean-devnet-transactions"));
        string fixtureDirectory = Path.GetFullPath(Value("fixtures", "tools/lean-ffi/target/bench-vectors"));
        ulong chainId = ulong.Parse(Value("chain-id", "10088288"), CultureInfo.InvariantCulture);
        ulong firstNonce = ulong.Parse(Value("nonce", "0"), CultureInfo.InvariantCulture);
        int rounds = int.Parse(Value("rounds", "1"), CultureInfo.InvariantCulture);
        bool sharedDependency = Value("shared-dependency", "false") == "true";
        int fixtureOffset = int.Parse(Value("fixture-offset", "0"), CultureInfo.InvariantCulture);
        string[] cases = Value("cases", "sphincs1,stark1,mixed4,sphincs16").Split(',');
        string[] modes = Value("wrapper-modes", "direct,recursive").Split(',');
        if (fixtureOffset < 0 || rounds is < 1 or > 32 || cases.Length is < 1 or > 16 || modes.Length is < 1 or > 2
            || modes.Distinct().Count() != modes.Length || modes.Any(m => m is not ("direct" or "recursive")))
            throw new ArgumentException("Invalid devnet case or wrapper bounds");
        BenchCase[] scenarios = cases.Select(ParseCase).ToArray();
        if (scenarios.Any(c => c.SharedSignature || c.SphincsCount > Eip8288Constants.MaxLeanSigDepsPerWrapper || c.StarkCount > Eip8288Constants.MaxLeanStarkDepsPerWrapper)
            || checked(rounds * scenarios.Length * modes.Length) > 128)
            throw new ArgumentException("Devnet requests exceed mempool wrapper limits");
        if (sharedDependency && scenarios.Any(c => c.SphincsCount != 1 || c.StarkCount != 0))
            throw new ArgumentException("Shared-dependency load requires SPHINCS1 only");
        using PrivateKey sender = new(Value("sender-key", "0x" + new string('0', 63) + "2"));
        NativeLeanProofVerifier verifier = NativeLeanProofVerifier.Instance;
        verifier.EnsureAvailable();
        Fixtures fixtures = new(fixtureDirectory);
        int signatureOffset = fixtureOffset, starkOffset = fixtureOffset;
        if (scenarios.Any(c => c.SphincsCount != 0) && (long)fixtureOffset + (sharedDependency ? 1 : scenarios.Sum(c => c.SphincsCount) * rounds * modes.Length) > fixtures.Sphincs.Length
            || scenarios.Any(c => c.StarkCount != 0) && (long)fixtureOffset + scenarios.Sum(c => c.StarkCount) * rounds * modes.Length > fixtures.Starks.Length)
            throw new InvalidOperationException("Insufficient fixtures for the requested offset and rounds");
        ulong nonce = firstNonce;
        List<object> requests = [];
        Directory.CreateDirectory(output);
        foreach (int round in Enumerable.Range(0, rounds))
            foreach (BenchCase scenario in scenarios)
                foreach (string mode in modes)
                {
                    if (sharedDependency) signatureOffset = fixtureOffset;
                    List<WitnessFixture> selected = [];
                    for (int i = 0; i < scenario.SphincsCount; i++)
                    {
                        if (signatureOffset >= fixtures.Sphincs.Length) throw new InvalidOperationException("Insufficient signature fixtures");
                        selected.Add(fixtures.Sphincs[signatureOffset++]);
                    }
                    if (scenario.StarkCount != 0)
                    {
                        if (starkOffset >= fixtures.Starks.Length) throw new InvalidOperationException("Insufficient STARK fixtures");
                        selected.Add(fixtures.Starks[starkOffset++]);
                    }
                    FrameDependency[] deps = Eip8288Dependencies.Canonicalize(selected.Select(f => f.Dependency)).ToArray();
                    if (deps.Length != selected.Count) throw new InvalidDataException("Devnet claims must be distinct");
                    Dictionary<FrameDependency, WitnessFixture> byDependency = selected.ToDictionary(f => f.Dependency);
                    selected = deps.Select(d => byDependency[d]).ToList();
                    foreach (WitnessFixture fixture in selected)
                    {
                        FrameDependency dep = fixture.Dependency;
                        bool verified = dep.Scheme == Eip8288Constants.LeanSphincsScheme
                            ? verifier.VerifyLeanSphincs(dep.DataHash, dep.VerificationKey, fixture.Witness)
                            : verifier.VerifyLeanStark(dep.DataHash, dep.VerificationKey, fixture.Witness);
                        if (!verified) throw new InvalidDataException("Devnet fixture did not verify");
                    }
                    ulong dependencyGas = checked((ulong)scenario.SphincsCount * Eip8288Constants.LeanSphincsVerificationGas
                        + (ulong)scenario.StarkCount * Eip8288Constants.LeanStarkVerificationGas);
                    TxFrame[] frames =
                    [
                        FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
                        new(FrameMode.DepVerify, FrameFlags.None, null, dependencyGas, UInt256.Zero, Eip8288Dependencies.Serialize(deps)),
                        new(FrameMode.Sender, FrameFlags.None, sender.Address, 50_000, UInt256.Zero, default)
                    ];
                    Transaction tx = new()
                    {
                        Type = TxType.FrameTx,
                        ChainId = chainId,
                        SenderAddress = sender.Address,
                        Nonce = nonce,
                        NonceKeys = [UInt256.Zero],
                        Frames = frames,
                        GasLimit = FrameTxValidation.TotalGasLimit(frames),
                        GasPrice = 1.GWei,
                        DecodedMaxFeePerGas = 100.GWei
                    };
                    FrameTxTestFrames.SignSecp256k1(tx, sender, sender.Address);
                    tx.Hash = tx.CalculateHash();
                    RecursiveStark? recursive = null;
                    byte[][]? direct = mode == "direct" ? selected.Select(f => f.Witness).ToArray() : null;
                    if (mode == "recursive")
                    {
                        AggregationInput input = new() { Deps = deps, Witnesses = selected.Select(f => (ReadOnlyMemory<byte>)f.Witness).ToArray() };
                        ValueHash256 commitment = Eip8288Dependencies.ComputeDepsHash(deps);
                        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, commitment);
                        if (!verifier.VerifyRecursiveStark(commitment, Eip8288Constants.AggregatedVk, proof))
                            throw new InvalidDataException("Devnet recursive proof did not verify");
                        recursive = new(proof, new Hash256(commitment));
                    }
                    MempoolWrapper wrapper = new()
                    {
                        Transactions = [new WrapperTransaction(tx)],
                        Deps = deps,
                        Mode = mode == "direct" ? MempoolWrapper.ModeDirect : MempoolWrapper.ModeRecursive,
                        Proofs = direct,
                        RecursiveStark = recursive
                    };
                    if (!MempoolWrapperValidator.Validate(wrapper, verifier, out string? validationError))
                        throw new InvalidDataException(validationError);
                    byte[] bytes = MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;
                    string name = $"{round:D2}-{scenario.Name}-{mode}-nonce{nonce}";
                    string requestFile = name + ".json";
                    WriteRequest(requestFile, bytes);
                    byte[][]? tamperedDirect = direct?.Select(w => w.ToArray()).ToArray();
                    RecursiveStark? tamperedRecursive = null;
                    if (tamperedDirect is not null) tamperedDirect[^1][^1] ^= 1;
                    else
                    {
                        byte[] proof = recursive!.StarkProof.ToArray();
                        proof[^1] ^= 1;
                        tamperedRecursive = new(proof, recursive.BlockDepsHash);
                    }
                    MempoolWrapper invalid = new()
                    {
                        Transactions = [new WrapperTransaction(tx)],
                        Deps = deps,
                        Mode = wrapper.Mode,
                        Proofs = tamperedDirect,
                        RecursiveStark = tamperedRecursive
                    };
                    if (MempoolWrapperValidator.Validate(invalid, verifier, out _))
                        throw new InvalidDataException("Tampered devnet witness unexpectedly verified");
                    byte[] tampered = MempoolWrapperDecoder.Instance.Encode(invalid).Bytes;
                    string negativeFile = name + "-invalid-proof.json";
                    WriteRequest(negativeFile, tampered);
                    requests.Add(new
                    {
                        name,
                        mode,
                        nonce,
                        sender = sender.Address.ToString(),
                        transactionHash = tx.Hash.ToString(),
                        rawTransaction = Hex(Rlp.Encode(tx).Bytes),
                        requestFile,
                        negativeRequestFile = negativeFile,
                        wrapperBytes = bytes.Length,
                        proofBytes = recursive?.StarkProof.Length ?? direct!.Sum(p => p.Length),
                        dependencies = deps.Select(d => new { scheme = $"0x{d.Scheme:x2}", dataHash = d.DataHash.ToString(), verificationKey = d.VerificationKey.ToString() }),
                        expectedReceiptStatus = "0x1"
                    });
                    nonce = checked(nonce + 1);
                }
        object manifest = new
        {
            chainId,
            sender = sender.Address.ToString(),
            firstNonce,
            fixtureOffset,
            sharedDependency,
            signatureFixturesConsumed = signatureOffset - fixtureOffset,
            starkFixturesConsumed = starkOffset - fixtureOffset,
            nextNonce = nonce,
            genesisRequirements = new
            {
                senderCode = "0x",
                senderBalanceWei = "100000000000000000000",
                nonce = firstNonce,
                forks = "Activate EIP-8288 together with the master Frame8250/8272/7906 composite; initialize native ABI 4 and the pinned guest key."
            },
            rpc = new { method = "eth_sendProofWrapper", parameter = "one 0x-prefixed RLP wrapper byte string", negativeOrder = "Submit invalid-proof requests before the corresponding valid nonce; expect RPC rejection and no pool entry." },
            genericCompression = "Generic STARKs are genuinely verified and carried in the aggregate; SPHINCS is recursively compressed.",
            requests
        };
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Generated {requests.Count} genuine signed requests for {sender.Address}; manifest {Path.Combine(output, "manifest.json")}");

        void WriteRequest(string name, byte[] bytes) => File.WriteAllText(Path.Combine(output, name),
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "eth_sendProofWrapper", @params = new[] { Hex(bytes) } }));
        static string Hex(byte[] bytes) => "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
        static BenchCase ParseCase(string value)
        {
            if (value == "stark1") return new(value, 0, 1);
            bool mixed = value.StartsWith("mixed", StringComparison.Ordinal);
            string prefix = mixed ? "mixed" : "sphincs";
            if (!value.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int signatures)
                || signatures < 1 || signatures > Eip8288Constants.MaxLeanSigDepsPerWrapper)
                throw new ArgumentException("Devnet cases must be sphincs1..16, stark1, or mixed1..16");
            return new(value, signatures, mixed ? 1 : 0);
        }
    }
}
