// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Tools.Eip8288Bench;

/// <summary>Distinct leanSPHINCS claims signed by the package's <c>fixtures</c> tool, generated once and outside every timed section.</summary>
/// <remarks>File layout: per record a 32-byte message then the <see cref="Eip8288Constants.LeanSphincsWitnessBytes"/>-byte witness.
/// The tool signs under the package's single test key, so claims differ by message only; the guest binds each (message, key)
/// claim the same way, so a distinct-key set costs the same circuit work per signature.</remarks>
internal sealed class SignaturePool
{
    private const int RecordBytes = 32 + Eip8288Constants.LeanSphincsWitnessBytes;

    private SignaturePool(ValueHash256 key, List<(ValueHash256 Message, byte[] Witness)> records)
    {
        Key = key;
        Records = records;
    }

    public ValueHash256 Key { get; }
    public List<(ValueHash256 Message, byte[] Witness)> Records { get; }

    public FrameDependency Dependency(int index) => new(Eip8288Constants.LeanSphincsScheme, Records[index].Message, Key);

    public static string VectorsDirectory => Path.Combine(AppContext.BaseDirectory, "lean-vectors");

    /// <summary>Loads <paramref name="path"/>, generating the missing tail so it holds at least <paramref name="count"/> claims.</summary>
    public static SignaturePool LoadOrCreate(string path, int count, int parallelism)
    {
        ValueHash256 key = new(File.ReadAllBytes(Path.Combine(VectorsDirectory, "sphincs-key.bin")));
        List<(ValueHash256, byte[])> records = [];
        if (File.Exists(path))
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length % RecordBytes != 0) throw new InvalidDataException($"{path} is not a signature pool");
            for (int offset = 0; offset < bytes.Length; offset += RecordBytes)
                records.Add((new ValueHash256(bytes.AsSpan(offset, 32)), bytes.AsSpan(offset + 32, Eip8288Constants.LeanSphincsWitnessBytes).ToArray()));
        }
        if (records.Count < count)
        {
            Stopwatch watch = Stopwatch.StartNew();
            (ValueHash256, byte[])[] generated = new (ValueHash256, byte[])[count - records.Count];
            int start = records.Count;
            Parallel.For(0, generated.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, i =>
            {
                ValueHash256 message = Message(start + i);
                generated[i] = (message, Sign(message));
            });
            using (FileStream stream = new(path, FileMode.Append))
                foreach ((ValueHash256 message, byte[] witness) in generated)
                {
                    stream.Write(message.Bytes);
                    stream.Write(witness);
                }
            records.AddRange(generated);
            Console.Error.WriteLine($"signed {generated.Length} claims in {watch.Elapsed.TotalSeconds:F1} s with {parallelism} workers");
        }
        SignaturePool pool = new(key, records);
        // A pool from another package version would fail inside the timed proving call; check one claim up front.
        if (!NativeLeanProofVerifier.Instance.VerifyLeanSphincs(pool.Records[0].Message, key, pool.Records[0].Witness))
            throw new InvalidDataException($"{path} does not verify under this package's leanSPHINCS key; delete it to regenerate");
        return pool;
    }

    private static ValueHash256 Message(int index)
    {
        Span<byte> seed = stackalloc byte[4 + 16];
        "eip8288-bench-v1"u8.CopyTo(seed);
        BinaryPrimitives.WriteInt32BigEndian(seed[16..], index);
        return ValueKeccak.Compute(seed);
    }

    private static byte[] Sign(ValueHash256 message)
    {
        string output = Path.GetTempFileName();
        try
        {
            using Process process = Process.Start(new ProcessStartInfo(Path.Combine(VectorsDirectory, "fixtures"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--sign-message", Convert.ToHexString(message.Bytes), output }
            })!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new TimeoutException("fixtures --sign-message did not finish in two minutes");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException($"fixtures --sign-message failed: {stderr.Result}{stdout.Result}");
            byte[] witness = File.ReadAllBytes(output);
            if (witness.Length != Eip8288Constants.LeanSphincsWitnessBytes) throw new InvalidDataException("unexpected witness length");
            return witness;
        }
        finally
        {
            File.Delete(output);
        }
    }
}
