// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using Nethermind.Tools.ProfilingMcp.Analysis;
using Nethermind.Tools.ProfilingMcp.EventPipe;

namespace Nethermind.Tools.ProfilingMcp.Native;

/// <summary>The address ranges of JIT-compiled .NET code, from the perf map the runtime writes, with their method names.</summary>
public sealed class JitMap
{
    public static JitMap Empty { get; } = new([]);

    private readonly (ulong Start, ulong End, string Name)[] _ranges;

    private JitMap((ulong Start, ulong End, string Name)[] ranges)
    {
        Array.Sort(ranges, static (a, b) => a.Start.CompareTo(b.Start));
        _ranges = ranges;
    }

    public int Count => _ranges.Length;

    public static JitMap Load(string path) => File.Exists(path) ? Parse(File.ReadLines(path)) : Empty;

    /// <summary>Parses perf map lines: start and size in hex, then the method's name.</summary>
    internal static JitMap Parse(IEnumerable<string> lines)
    {
        List<(ulong, ulong, string)> ranges = [];
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length == 3 && TryParseHex(parts[0], out ulong start) && TryParseHex(parts[1], out ulong size) && size > 0)
                ranges.Add((start, start + size, CleanMethodName(parts[2])));
        }

        return new JitMap([.. ranges]);
    }

    public string? Find(ulong address)
    {
        int low = 0, high = _ranges.Length - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            if (_ranges[middle].Start <= address)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return high >= 0 && address < _ranges[high].End ? _ranges[high].Name : null;
    }

    private static bool TryParseHex(string text, out ulong value) =>
        ulong.TryParse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.AsSpan(2) : text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Turns a perf map name such as <c>instance bool [Asm] Ns.Type`1[T]::Method(int32)[OptimizedTier1]</c> into <c>Ns.Type`1.Method</c>,
    /// the form EventPipe traces use, so the same patterns find a method in either.
    /// </summary>
    internal static string CleanMethodName(string name)
    {
        int arguments = name.IndexOf('(');
        if (arguments > 0)
            name = name[..arguments];

        // The name is the last word; the return type before it may have spaces inside its generic arguments.
        int depth = 0;
        int start = 0;
        for (int i = name.Length - 1; i >= 0 && start == 0; i--)
        {
            switch (name[i])
            {
                case ']' or '>':
                    depth++;
                    break;
                case '[' or '<':
                    depth--;
                    break;
                case ' ' when depth == 0:
                    start = i + 1;
                    break;
            }
        }

        return NetTraceReader.CleanFrameName(name[start..].Replace("::", ".", StringComparison.Ordinal).Replace('/', '+'));
    }
}

/// <summary>Folds the samples printed by <c>perf script -F comm,tid,ip,sym,dso</c> into counted stacks, outermost frame first.</summary>
public sealed class PerfScriptFolder(JitMap jit)
{
    private const ulong KernelAddresses = 0xffff_8000_0000_0000;

    private readonly Dictionary<string, long> _stacks = new(StringComparer.Ordinal);
    private readonly List<string> _frames = [];
    private string? _thread;

    public void Add(string line)
    {
        if (line.Length == 0)
        {
            Complete();
            return;
        }

        if (!char.IsWhiteSpace(line[0]))
        {
            Complete();
            // "comm tid", where the thread name may contain spaces.
            string header = line.TrimEnd();
            int space = header.LastIndexOf(' ');
            _thread = (space > 0 ? header[..space] : header).Trim();
            return;
        }

        string frame = FrameName(line.Trim());
        // Unnamed frames of one library are one block of time; a run of them says nothing more than one.
        if (!(frame.StartsWith('[') && _frames.Count > 0 && _frames[^1] == frame))
            _frames.Add(frame);
    }

    /// <summary>The stacks in folded form, <c>outer;...;inner count</c>, as flame graph tools read them.</summary>
    public IEnumerable<string> Fold()
    {
        Complete();
        return _stacks.OrderByDescending(static s => s.Value).Select(static s => $"{s.Key} {s.Value.ToString(CultureInfo.InvariantCulture)}");
    }

    private void Complete()
    {
        if (_thread is null)
            return;

        StringBuilder stack = new($"[thread {_thread}]");
        for (int i = _frames.Count - 1; i >= 0; i--)
            stack.Append(';').Append(_frames[i]);

        string key = stack.ToString();
        _stacks[key] = _stacks.GetValueOrDefault(key) + 1;
        _frames.Clear();
        _thread = null;
    }

    /// <summary>Names a frame line such as <c>7f12 rocksdb::DB::Get(...) (/lib/librocksdb.so)</c>.</summary>
    private string FrameName(string line)
    {
        int space = line.IndexOf(' ');
        string address = space > 0 ? line[..space] : line;
        string rest = space > 0 ? line[(space + 1)..] : "";
        int module = ModuleStart(rest);
        string symbol = module > 0 ? rest[..module].TrimEnd() : rest;
        string library = module > 0 ? rest[(module + 1)..^1] : "";

        if (symbol != "[unknown]" && symbol.Length > 0)
            return $"{Path.GetFileName(library)}!{NativeName(symbol)}".Replace(';', ',');

        ulong ip = ulong.Parse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (jit.Find(ip) is { } method)
            return method;
        if (ip >= KernelAddresses)
            return "[kernel]";
        return library is "" or "[unknown]" ? "[unknown]" : $"[{Path.GetFileName(library.Replace(" (deleted)", "", StringComparison.Ordinal))}]";
    }

    /// <summary>Finds the parenthesis that opens the module at the end of a frame, which may itself end in <c>(deleted)</c>.</summary>
    private static int ModuleStart(string rest)
    {
        if (!rest.EndsWith(')'))
            return -1;

        int depth = 0;
        for (int i = rest.Length - 1; i >= 0; i--)
        {
            if (rest[i] == ')')
                depth++;
            else if (rest[i] == '(' && --depth == 0)
                return i;
        }

        return -1;
    }

    /// <summary>Drops a C++ symbol's parameter list, keeping <c>operator()</c>.</summary>
    internal static string NativeName(string symbol)
    {
        int search = symbol.IndexOf("operator()", StringComparison.Ordinal) is var op and >= 0 ? op + "operator()".Length : 0;
        int arguments = symbol.IndexOf('(', search);
        return arguments > 0 ? symbol[..arguments] : symbol;
    }
}

/// <summary>Reads folded stacks written by <see cref="NativeSampler"/> as per-method times with every call path.</summary>
public static class FoldedStacks
{
    /// <summary>Frames kept per stack; perf records at most 127 by default, and a file passed in may hold anything.</summary>
    internal const int MaxFrames = 512;

    /// <param name="millisecondsPerSample">CPU time one sample stands for.</param>
    public static IReadOnlyList<FunctionStat> Read(IEnumerable<string> lines, double millisecondsPerSample = 1000.0 / NativeSampler.Hz)
    {
        NetTraceReader.PathNode root = new("", null);
        foreach (string line in lines)
        {
            int space = line.LastIndexOf(' ');
            if (space <= 0 || !long.TryParse(line.AsSpan(space + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long samples))
                continue;

            NetTraceReader.PathNode node = root;
            string[] frames = line[..space].Split(';');
            // The innermost frames, which carry the time, are kept; the cut is marked so the stack does not look complete.
            int first = frames.Length > MaxFrames ? frames.Length - MaxFrames + 1 : 0;
            if (first > 0)
                node = node.Child($"[{first:N0} outer frames]");
            for (int i = first; i < frames.Length; i++)
                node = node.Child(frames[i]);
            node.Own += samples * millisecondsPerSample;
            node.Samples += samples;
        }

        return NetTraceReader.ToFunctions(root);
    }
}
