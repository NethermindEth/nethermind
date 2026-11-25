// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
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

    /// <summary>Far more than a large process's map holds: Nethermind's has a few hundred thousand entries in tens of MB.</summary>
    internal const long MaxFileBytes = 512L << 20;

    internal const int MaxRanges = 4_000_000;

    /// <summary>Reads the perf map at <paramref name="path"/>, or returns <see cref="Empty"/> when there is none.</summary>
    /// <remarks>
    /// The map is the target's file, read by the privileged sidecar, so it is bounded: only as many bytes as the file had when opened, which
    /// leaves out a device such as <c>/dev/zero</c> planted at its path, and only <paramref name="maxBytes"/> of them. It is opened in a way
    /// that cannot wait on a FIFO planted there.
    /// </remarks>
    /// <exception cref="InvalidDataException">The map is larger than the limits, or is not a file.</exception>
    public static JitMap Load(string path, long maxBytes = MaxFileBytes, int maxRanges = MaxRanges)
    {
        if (!File.Exists(path))
            return Empty;

        using FileStream file = OpenForReading(path);
        long length = file.Length;
        if (length > maxBytes)
            throw new InvalidDataException($"The perf map {path} is {length:N0} bytes, more than the {maxBytes:N0} read.");

        byte[] content = GC.AllocateUninitializedArray<byte>((int)length);
        int read = file.ReadAtLeast(content, content.Length, throwOnEndOfStream: false);
        using StreamReader reader = new(new MemoryStream(content, 0, read));
        return Parse(Lines(reader), maxRanges);

        static IEnumerable<string> Lines(StreamReader reader)
        {
            while (reader.ReadLine() is { } line)
                yield return line;
        }
    }

    /// <summary>Opens <paramref name="path"/> for reading, refusing a FIFO or socket rather than waiting on it.</summary>
    /// <remarks>
    /// Opening a FIFO for reading waits until something opens it for writing, which a target that planted one need never do, and the open cannot
    /// be cancelled. On Linux the file is opened with <c>O_NONBLOCK</c>, which returns at once, and is refused when it cannot seek, as a FIFO cannot.
    /// </remarks>
    /// <exception cref="InvalidDataException">The path is not a file that can be read from start to end.</exception>
    internal static FileStream OpenForReading(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        const int ReadOnly = 0, NonBlocking = 0x800, CloseOnExec = 0x80000;
        SafeFileHandle handle = new(Open(path, ReadOnly | NonBlocking | CloseOnExec), ownsHandle: true);
        if (handle.IsInvalid)
            throw new IOException($"Cannot open {path}: error {Marshal.GetLastPInvokeError()}.");

        FileStream? file = null;
        try
        {
            file = new FileStream(handle, FileAccess.Read, bufferSize: 0);
            return file.CanSeek ? file : throw new InvalidDataException($"{path} is not a file.");
        }
        catch
        {
            if (file is null)
                handle.Dispose();
            else
                file.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    /// <summary>Parses perf map lines: start and size in hex, then the method's name.</summary>
    /// <exception cref="InvalidDataException">There are more than <paramref name="maxRanges"/> ranges.</exception>
    internal static JitMap Parse(IEnumerable<string> lines, int maxRanges = MaxRanges)
    {
        List<(ulong, ulong, string)> ranges = [];
        foreach (string line in lines)
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length == 3 && TryParseHex(parts[0], out ulong start) && TryParseHex(parts[1], out ulong size) && size > 0)
            {
                if (ranges.Count == maxRanges)
                    throw new InvalidDataException($"The perf map has more than {maxRanges:N0} entries.");
                ranges.Add((start, start + size, CleanMethodName(parts[2])));
            }
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

    private readonly Dictionary<string, long> _stacks = [with(StringComparer.Ordinal)];
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
        List<KeyValuePair<string, long>> stacks = [.. _stacks];
        Sorting.SortDescending(stacks, static s => s.Value);
        return Lines(stacks);

        static IEnumerable<string> Lines(List<KeyValuePair<string, long>> stacks)
        {
            foreach ((string stack, long count) in stacks)
                yield return $"{stack} {count.ToString(CultureInfo.InvariantCulture)}";
        }
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

        // perf names JIT-compiled frames itself when it finds the runtime's perf map, but in the map's raw form; they are looked up first so
        // every managed frame gets the same clean name.
        ulong ip = ulong.Parse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (jit.Find(ip) is { } method)
            return method;
        if (symbol != "[unknown]" && symbol.Length > 0)
            return $"{Path.GetFileName(library)}!{NativeName(symbol)}".Replace(';', ',');
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
