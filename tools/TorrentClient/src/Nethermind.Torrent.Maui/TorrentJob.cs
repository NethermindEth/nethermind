// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nethermind.Torrent.Maui;

internal sealed class TorrentJob : INotifyPropertyChanged
{
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB"];
    private string _name = "New torrent";
    private string _status = "Queued";
    private string _phase = "Queued";
    private string _message = string.Empty;
    private string _outputDirectory;
    private long _totalBytes;
    private long _downloadedBytes;
    private int _pieceCount;
    private int _completedPieces;
    private int _activePeers;
    private int _knownPeers;
    private bool _isRunning;
    private bool _isComplete;
    private bool _isChecking;
    private bool _hasDataToResume;
    private double _progress;
    private DateTimeOffset _lastProgressAt = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private long _lastDownloadedBytes;
    private DateTimeOffset _lastSpeedSampleAt = DateTimeOffset.UtcNow;
    private double _downloadRateBytesPerSecond;
    private bool? _effectiveDht;
    private bool? _effectiveTrackers;
    private readonly Lock _pendingLogLock = new();
    private readonly Queue<string> _pendingLogLines = new();

    public TorrentJob(string torrentPath, string outputDirectory)
    {
        TorrentPath = torrentPath;
        _outputDirectory = outputDirectory;
        Name = Path.GetFileNameWithoutExtension(torrentPath);
    }

    public string TorrentPath { get; }

    public string InfoHashHex { get; set; } = string.Empty;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetField(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(TotalText));
                OnPropertyChanged(nameof(RemainingText));
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (value > 0)
            {
                _hasDataToResume = true;
            }

            if (SetField(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(DownloadedText));
                OnPropertyChanged(nameof(RemainingText));
            }
        }
    }

    public int PieceCount
    {
        get => _pieceCount;
        set
        {
            if (SetField(ref _pieceCount, value))
            {
                OnPropertyChanged(nameof(PiecesText));
            }
        }
    }

    public int CompletedPieces
    {
        get => _completedPieces;
        set
        {
            if (SetField(ref _completedPieces, value))
            {
                OnPropertyChanged(nameof(PiecesText));
            }
        }
    }

    public int ActivePeers
    {
        get => _activePeers;
        set => SetField(ref _activePeers, value);
    }

    public int KnownPeers
    {
        get => _knownPeers;
        set => SetField(ref _knownPeers, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set => SetField(ref _isRunning, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetField(ref _isComplete, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetField(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (SetField(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public DateTimeOffset LastProgressAt
    {
        get => _lastProgressAt;
        set => SetField(ref _lastProgressAt, value);
    }

    public double DownloadRateBytesPerSecond
    {
        get => _downloadRateBytesPerSecond;
        set
        {
            if (SetField(ref _downloadRateBytesPerSecond, value))
            {
                OnPropertyChanged(nameof(DownloadRateText));
            }
        }
    }

    public ObservableCollection<TorrentFileItem> Files { get; } = [];

    public ObservableCollection<string> Trackers { get; } = [];

    public List<string> ExplicitPeers { get; } = [];

    public string MagnetUri
    {
        get
        {
            StringBuilder link = new("magnet:?xt=urn:btih:");
            link.Append(InfoHashHex);
            AppendMagnetParameter(link, "dn", Name);
            foreach (string peer in ExplicitPeers)
            {
                AppendMagnetParameter(link, "x.pe", peer);
            }

            foreach (string tracker in Trackers.Take(64))
            {
                AppendMagnetParameter(link, "tr", tracker);
            }

            return link.ToString();
        }
    }

    private static void AppendMagnetParameter(StringBuilder link, string name, string value)
    {
        if (value.Length > MagnetLink.MaxUriLength)
        {
            return;
        }

        string encoded = Uri.EscapeDataString(value);
        if (link.Length + name.Length + encoded.Length + 2 <= MagnetLink.MaxUriLength)
        {
            link.Append('&').Append(name).Append('=').Append(encoded);
        }
    }

    public ObservableCollection<string> PeerEvents { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public string TotalText => FormatBytes(TotalBytes);

    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string RemainingText => FormatBytes(Math.Max(0, TotalBytes - DownloadedBytes));

    public string PiecesText => $"{CompletedPieces}/{PieceCount}";

    public string ProgressText => (Progress * 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public string? PayloadPath
    {
        get
        {
            if (Files.Count == 0)
            {
                return null;
            }

            string firstPath = Files[0].Path;
            int separator = firstPath.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string topLevel = separator < 0 ? firstPath : firstPath[..separator];
            return Path.GetFullPath(Path.Combine(OutputDirectory, topLevel));
        }
    }

    public IReadOnlyList<TorrentFileItem> FindFiles(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Files;
        }

        string term = query.Trim();
        List<TorrentFileItem> matches = [];
        foreach (TorrentFileItem file in Files)
        {
            if (file.Path.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(file);
            }
        }

        return matches;
    }

    public string ResolveFilePath(TorrentFileItem file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string root = Path.GetFullPath(OutputDirectory);
        string path = Path.GetFullPath(Path.Combine(root, file.Path));
        string relative = Path.GetRelativePath(root, path);
        if (relative is "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
        {
            throw new InvalidOperationException("Torrent file path escapes the output directory.");
        }

        return path;
    }

    public string DownloadRateText => FormatBytes((long)DownloadRateBytesPerSecond) + "/s";

    public string EffectiveDhtText => FormatEnabled(_effectiveDht);

    public string EffectiveTrackersText => FormatEnabled(_effectiveTrackers);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanStart => PieceCount > 0 && !IsRunning && !IsComplete && !IsChecking;

    public bool HasDataToResume => _hasDataToResume;

    public bool CanStop => IsRunning;

    public void ApplyMetadata(TorrentMetadata metadata)
    {
        _hasDataToResume = false;
        InfoHashHex = metadata.InfoHashHex;
        Name = metadata.Name;
        TotalBytes = metadata.TotalLength;
        PieceCount = metadata.PieceCount;
        CompletedPieces = 0;
        Progress = 0;
        Files.Clear();
        for (int i = 0; i < metadata.Files.Count; i++)
        {
            Files.Add(new TorrentFileItem(metadata.Files[i]));
        }

        Trackers.Clear();
        for (int i = 0; i < metadata.Trackers.Count; i++)
        {
            Trackers.Add(metadata.Trackers[i].ToString());
        }

        OnPropertyChanged(nameof(CanStart));
    }

    public void BeginVerification()
    {
        DownloadedBytes = 0;
        CompletedPieces = 0;
        Progress = 0;
        IsChecking = true;
        Status = "Checking";
        Phase = "Verifying";
        Message = "Checking existing data";
    }

    public void ApplyVerificationProgress(TorrentVerificationProgress verification)
    {
        DownloadedBytes = verification.VerifiedBytes;
        CompletedPieces = verification.VerifiedPieces;
        Progress = TotalBytes == 0 ? 0 : (double)DownloadedBytes / TotalBytes;
        Message = $"Checked {verification.ScannedPieces}/{verification.TotalPieces} pieces";
    }

    public void CompleteVerification(TorrentVerificationProgress verification)
    {
        ApplyVerificationProgress(verification);
        bool complete = verification.VerifiedPieces == verification.TotalPieces;
        IsComplete = complete;
        IsChecking = false;
        Status = complete ? "Complete" : "Paused";
        Phase = complete ? "Completed" : "Paused";
        Message = $"Verified {verification.VerifiedPieces}/{verification.TotalPieces} pieces";
    }

    public void AbortVerification(string status, string message)
    {
        DownloadedBytes = 0;
        CompletedPieces = 0;
        Progress = 0;
        IsChecking = false;
        Status = status;
        Phase = "Queued";
        Message = message;
    }

    public void AttachRun(Task runTask, CancellationTokenSource cancellation)
    {
        _runTask = runTask;
        _cancellation = cancellation;
        _lastDownloadedBytes = DownloadedBytes;
        _lastSpeedSampleAt = DateTimeOffset.UtcNow;
        DownloadRateBytesPerSecond = 0;
        IsRunning = true;
        IsComplete = false;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public void ApplyEffectiveOptions(TorrentClientOptions options)
    {
        _effectiveDht = options.EnableDht;
        _effectiveTrackers = options.EnableTrackers;
        OnPropertyChanged(nameof(EffectiveDhtText));
        OnPropertyChanged(nameof(EffectiveTrackersText));
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? runTask = _runTask;
        if (cancellation is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void DetachRun(bool completed)
    {
        _cancellation?.Dispose();
        _cancellation = null;
        _runTask = null;
        IsRunning = false;
        IsComplete = completed;
        DownloadRateBytesPerSecond = 0;
        ActivePeers = 0;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public void AppendLog(string line)
    {
        if (LogLines.Count >= 400)
        {
            LogLines.RemoveAt(0);
        }

        LogLines.Add($"{DateTimeOffset.Now:HH:mm:ss}  {line}");
        if (line.StartsWith("peer ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("dht ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" peers", StringComparison.OrdinalIgnoreCase))
        {
            if (PeerEvents.Count >= 120)
            {
                PeerEvents.RemoveAt(0);
            }

            PeerEvents.Add(line);
        }
    }

    public void QueueLog(string line)
    {
        lock (_pendingLogLock)
        {
            if (_pendingLogLines.Count >= 200)
            {
                _pendingLogLines.Dequeue();
            }

            _pendingLogLines.Enqueue(line);
        }
    }

    public void DrainLogs(int maxLines)
    {
        List<string> lines;
        lock (_pendingLogLock)
        {
            if (_pendingLogLines.Count == 0 || maxLines <= 0)
            {
                return;
            }

            lines = new(Math.Min(maxLines, _pendingLogLines.Count));
            while (lines.Count < maxLines && _pendingLogLines.TryDequeue(out string? line))
            {
                lines.Add(line);
            }
        }

        foreach (string line in lines)
        {
            AppendLog(line);
        }
    }

    public void ApplyProgress(TorrentSessionProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.TorrentName))
        {
            Name = progress.TorrentName;
        }

        Phase = progress.Phase.ToString();
        Message = progress.Message;
        TotalBytes = progress.TotalBytes;
        DownloadedBytes = progress.DownloadedBytes;
        PieceCount = progress.PieceCount;
        CompletedPieces = progress.CompletedPieces;
        ActivePeers = progress.ActivePeers;
        KnownPeers = progress.KnownPeers;
        LastProgressAt = progress.Timestamp;
        Progress = TotalBytes == 0 ? 0 : Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
        UpdateSpeed(progress.DownloadedBytes, progress.Timestamp);
        Status = progress.Phase == TorrentSessionPhase.Completed ? "Complete" : progress.Phase.ToString();
    }

    private void UpdateSpeed(long downloadedBytes, DateTimeOffset timestamp)
    {
        double seconds = (timestamp - _lastSpeedSampleAt).TotalSeconds;
        if (seconds < 0.5)
        {
            return;
        }

        DownloadRateBytesPerSecond = Math.Max(0, (downloadedBytes - _lastDownloadedBytes) / seconds);
        _lastDownloadedBytes = downloadedBytes;
        _lastSpeedSampleAt = timestamp;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    internal static string FormatBytes(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
    }

    private static string FormatEnabled(bool? value)
        => value switch
        {
            true => "Enabled",
            false => "Disabled",
            null => "Not started",
        };
}

internal sealed class TorrentFileItem(TorrentFileEntry entry)
{
    public string Path { get; } = entry.Path;

    public long Length { get; } = entry.Length;

    public string LengthText { get; } = TorrentJob.FormatBytes(entry.Length);
}
