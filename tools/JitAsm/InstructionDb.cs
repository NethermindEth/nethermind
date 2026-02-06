// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace JitAsm;

internal sealed class InstructionInfo
{
    public required string Mnemonic { get; init; }
    public required string OperandPattern { get; init; }
    public float Throughput { get; init; }
    public int Latency { get; init; }
    public int Uops { get; init; }
    public string? Ports { get; init; }
}

internal sealed class InstructionDb
{
    private const string Magic = "UOPS";
    private const ushort Version = 2;

    private readonly Dictionary<string, List<InstructionInfo>> _instructions = new(StringComparer.OrdinalIgnoreCase);

    public string ArchName { get; }

    public InstructionDb(string archName)
    {
        ArchName = archName;
    }

    public void Add(InstructionInfo info)
    {
        string key = info.Mnemonic.ToLowerInvariant();
        if (!_instructions.TryGetValue(key, out var list))
        {
            list = [];
            _instructions[key] = list;
        }
        list.Add(info);
    }

    public int Count => _instructions.Sum(kv => kv.Value.Count);

    public InstructionInfo? Lookup(string mnemonic, string operandPattern)
    {
        if (!_instructions.TryGetValue(mnemonic, out var forms))
            return null;

        // Exact match
        foreach (var form in forms)
        {
            if (string.Equals(form.OperandPattern, operandPattern, StringComparison.OrdinalIgnoreCase))
                return form;
        }

        // Relaxed match: ignore register width differences (r32 ≈ r64 for same instruction class)
        string relaxed = RelaxPattern(operandPattern);
        foreach (var form in forms)
        {
            if (string.Equals(RelaxPattern(form.OperandPattern), relaxed, StringComparison.OrdinalIgnoreCase))
                return form;
        }

        // Mnemonic-only match for zero-operand instructions (ret, nop, etc.)
        if (operandPattern.Length == 0)
        {
            foreach (var form in forms)
            {
                if (form.OperandPattern.Length == 0)
                    return form;
            }
        }

        return null;
    }

    private static string RelaxPattern(string pattern)
    {
        // Normalize register widths: r8/r16/r32/r64 → r, m8/m16/m32/m64/m128/m256/m512 → m, imm8/imm32 → imm
        return pattern
            .Replace("r64", "r").Replace("r32", "r").Replace("r16", "r").Replace("r8", "r")
            .Replace("m512", "m").Replace("m256", "m").Replace("m128", "m")
            .Replace("m64", "m").Replace("m32", "m").Replace("m16", "m").Replace("m8", "m")
            .Replace("imm32", "imm").Replace("imm8", "imm");
    }

    public void Save(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // Header
        writer.Write(Magic.ToCharArray());
        writer.Write(Version);
        writer.Write(ArchName);

        // Count all entries
        int entryCount = Count;
        writer.Write(entryCount);

        // Entries
        foreach (var (_, forms) in _instructions)
        {
            foreach (var info in forms)
            {
                writer.Write(info.Mnemonic);
                writer.Write(info.OperandPattern);
                writer.Write(info.Throughput);
                writer.Write((short)info.Latency);
                writer.Write((short)info.Uops);
                writer.Write(info.Ports ?? string.Empty);
            }
        }
    }

    public static InstructionDb Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // Header
        char[] magic = reader.ReadChars(4);
        if (new string(magic) != Magic)
            throw new InvalidDataException($"Invalid instruction database file: bad magic");

        ushort version = reader.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"Unsupported instruction database version: {version}");

        string archName = reader.ReadString();
        int entryCount = reader.ReadInt32();

        var db = new InstructionDb(archName);

        for (int i = 0; i < entryCount; i++)
        {
            string mnemonic = reader.ReadString();
            string operandPattern = reader.ReadString();
            float throughput = reader.ReadSingle();
            short latency = reader.ReadInt16();
            short uops = reader.ReadInt16();
            string ports = reader.ReadString();

            db.Add(new InstructionInfo
            {
                Mnemonic = mnemonic,
                OperandPattern = operandPattern,
                Throughput = throughput,
                Latency = latency,
                Uops = uops,
                Ports = ports.Length > 0 ? ports : null
            });
        }

        return db;
    }
}
