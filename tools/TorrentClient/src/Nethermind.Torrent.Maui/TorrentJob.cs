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
    private int _activeUploadPeers;
    private int _knownPeers;
    private long _payloadBytesReceived;
    private long _verifiedBytesFromPeers;
    private long _uploadedBytes;
    private long _activeTicks;
    private int _lastRunContributors;
    private bool _hasTransferHistory;
    private long _runBaseBytes;
    private long _runBaseVerifiedBytes;
    private long _runBaseUploadedBytes;
    private long _runBaseTicks;
    private TorrentSession? _session;
    private bool _isRunning;
    private bool _isComplete;
    private bool _isChecking;
    private bool _resumeSeeding;
    private bool _isPrivate;
    private bool _hasDataToResume;
    private double _progress;
    private DateTimeOffset _lastProgressAt = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
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
        set
        {
            if (SetField(ref _outputDirectory, value))
            {
                OnPropertyChanged(nameof(OutputFolderText));
            }
        }
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StageStateText));
                OnPropertyChanged(nameof(StageActionText));
                OnPropertyChanged(nameof(StageActionAvailable));
                OnPropertyChanged(nameof(QueueDetailText));
                OnPropertyChanged(nameof(OverviewSummaryText));
            }
        }
    }

    public string Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    public string Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(QueueDetailText));
                OnPropertyChanged(nameof(OverviewSummaryText));
            }
        }
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
                OnPropertyChanged(nameof(VerifiedSummaryText));
                OnPropertyChanged(nameof(QueueDetailText));
                OnPropertyChanged(nameof(RemainingSummaryText));
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
                OnPropertyChanged(nameof(VerifiedSummaryText));
                OnPropertyChanged(nameof(QueueDetailText));
                OnPropertyChanged(nameof(RemainingSummaryText));
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

    public int ActiveUploadPeers
    {
        get => _activeUploadPeers;
        private set => SetField(ref _activeUploadPeers, value);
    }

    public int KnownPeers
    {
        get => _knownPeers;
        set => SetField(ref _knownPeers, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ContributorText));
                OnPropertyChanged(nameof(StageActionText));
                OnPropertyChanged(nameof(StageActionAvailable));
            }
        }
    }

    public bool IsComplete
    {
        get => _isComplete;
        set
        {
            if (SetField(ref _isComplete, value))
            {
                OnPropertyChanged(nameof(StageStateText));
                OnPropertyChanged(nameof(StageActionText));
                OnPropertyChanged(nameof(StageActionAvailable));
                OnPropertyChanged(nameof(RemainingSummaryText));
                OnPropertyChanged(nameof(QueueDetailText));
                OnPropertyChanged(nameof(OverviewSummaryText));
            }
        }
    }

    public bool ResumeSeeding
    {
        get => _resumeSeeding;
        set => SetField(ref _resumeSeeding, value);
    }

    public int? ActiveListenPort { get; private set; }

    public string ListeningPortText => ActiveListenPort?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Not listening";

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetField(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(StageActionText));
                OnPropertyChanged(nameof(StageActionAvailable));
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

    public string ProgressText => Progress >= 1 ? "100%" : (Progress * 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public string VerifiedSummaryText => $"{DownloadedText} of {TotalText} verified locally";

    public string NetworkReceivedText => _hasTransferHistory ? FormatBytes(_payloadBytesReceived) : "Not recorded";

    public string VerifiedFromPeersText => _hasTransferHistory ? FormatBytes(_verifiedBytesFromPeers) : "Not recorded";

    public string UploadedText => _hasTransferHistory ? FormatBytes(_uploadedBytes) : "Not recorded";

    public string ActiveAverageText => !_hasTransferHistory ? "Not recorded" : _activeTicks == 0 ? "Not yet measured" :
        FormatBytes((long)Math.Min(long.MaxValue, _verifiedBytesFromPeers / TimeSpan.FromTicks(_activeTicks).TotalSeconds)) + "/s";

    public string ActiveTimeText
    {
        get
        {
            TimeSpan time = TimeSpan.FromTicks(_activeTicks);
            return time.TotalHours >= 1 ? $"{(long)time.TotalHours}h {time.Minutes}m" :
                time.TotalMinutes >= 1 ? $"{time.Minutes}m {time.Seconds}s" : $"{time.Seconds}s";
        }
    }

    public string ContributorText => _hasTransferHistory
        ? $"{_lastRunContributors} {(IsRunning ? "current" : "last")} run" : "Not recorded";

    public TorrentTransferHistory? TransferHistory => _hasTransferHistory
        ? new TorrentTransferHistory(_payloadBytesReceived, _verifiedBytesFromPeers, _activeTicks, _lastRunContributors, _uploadedBytes) : null;

    public string RemainingSummaryText => IsComplete ? string.Empty : $"{RemainingText} to complete";

    public string OutputFolderText
    {
        get
        {
            string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(OutputDirectory));
            return folder.Length == 0 ? OutputDirectory : folder;
        }
    }

    public string QueueDetailText => Status switch
    {
        "Seeding" => $"{DownloadedText} verified locally \u00b7 {UploadedText} uploaded",
        "Complete" => $"{DownloadedText} verified locally",
        "Paused" when IsComplete => $"{DownloadedText} verified locally \u00b7 seeding paused",
        "Paused" => $"{DownloadedText} verified \u00b7 {RemainingText} left",
        _ => Message,
    };

    public string StageStateText => Status switch
    {
        "Complete" => "Content verified",
        "Paused" when IsComplete => "Seeding paused",
        "Seeding" => "Seeding",
        "Checking" => "Checking existing data",
        "Paused" => "Paused",
        "Error" => "Needs attention",
        "DiscoveringPeers" => "Finding peers",
        "InitializingStorage" => "Preparing files",
        "LoadingMetadata" => "Loading metadata",
        _ => Status,
    };

    public string StageActionText
    {
        get
        {
            if (IsRunning) return "Pause";
            if (!CanStart) return string.Empty;
            if (IsComplete) return Status == "Paused" ? "Resume seeding" : "Seed";
            return Status is "Queued" or "Ready" ? "Start" : "Resume";
        }
    }

    public bool StageActionAvailable => StageActionText.Length > 0;

    public string OverviewSummaryText => Status switch
    {
        "Paused" when IsComplete => "Seeding paused. Verified content is available locally.",
        "Paused" => "Resume when you are ready to find peers and finish the remaining data.",
        "Error" => Message.Length == 0 ? "Check Activity for details." : Message,
        _ => Message,
    };

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

    public string EffectiveDhtText => FormatEnabled(_isPrivate ? false : _effectiveDht);

    public bool IsPrivate => _isPrivate;

    public string EffectiveTrackersText => FormatEnabled(_effectiveTrackers);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanStart => PieceCount > 0 && !IsRunning && !IsChecking;

    public bool ShouldResumeSeeding => ResumeSeeding && IsComplete && CanStart;

    public bool HasDataToResume => _hasDataToResume;

    public bool CanStop => IsRunning;

    public void ApplyMetadata(TorrentMetadata metadata)
    {
        _hasDataToResume = false;
        _isPrivate = metadata.IsPrivate;
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
        OnPropertyChanged(nameof(EffectiveDhtText));
        OnPropertyChanged(nameof(StageActionText));
        OnPropertyChanged(nameof(StageActionAvailable));
    }

    public void BeginVerification()
    {
        DownloadedBytes = 0;
        CompletedPieces = 0;
        Progress = 0;
        IsComplete = false;
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
        if (!complete)
        {
            ResumeSeeding = false;
        }
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

    public void RestoreTransferHistory(TorrentTransferHistory? history)
    {
        if (history is null)
        {
            return;
        }

        _hasTransferHistory = true;
        _payloadBytesReceived = history.PayloadBytesReceived;
        _verifiedBytesFromPeers = history.VerifiedBytesFromPeers;
        _uploadedBytes = history.UploadedBytes;
        _activeTicks = history.ActiveTicks;
        _lastRunContributors = history.LastRunContributors;
        NotifyTransferChanged();
    }

    public void AttachRun(Task runTask, CancellationTokenSource cancellation, TorrentSession? session = null, int? listenPort = null)
    {
        _runTask = runTask;
        _cancellation = cancellation;
        _session = session;
        _runBaseBytes = _payloadBytesReceived;
        _runBaseVerifiedBytes = _verifiedBytesFromPeers;
        _runBaseUploadedBytes = _uploadedBytes;
        _runBaseTicks = _activeTicks;
        _lastRunContributors = 0;
        _hasTransferHistory = true;
        ActiveListenPort = listenPort;
        OnPropertyChanged(nameof(ListeningPortText));
        NotifyTransferChanged();
        IsRunning = true;
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

    public async Task StopAsync(bool preserveSeedingIntent = true)
    {
        CancellationTokenSource? cancellation = _cancellation;
        Task? runTask = _runTask;
        if (cancellation is null)
        {
            return;
        }

        if (_session?.GetLatestProgress() is TorrentSessionProgress progress && progress.Timestamp > LastProgressAt)
        {
            ApplyProgress(progress);
        }

        if (!preserveSeedingIntent)
        {
            ResumeSeeding = false;
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
        _session = null;
        ActiveListenPort = null;
        OnPropertyChanged(nameof(ListeningPortText));
        IsRunning = false;
        IsComplete = completed || IsComplete;
        ActivePeers = 0;
        ActiveUploadPeers = 0;
        KnownPeers = 0;
        Phase = Status;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public bool IsCurrentSession(TorrentSession session) => ReferenceEquals(_session, session);

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
        if (progress.Timestamp <= LastProgressAt)
        {
            return;
        }

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
        IsComplete = PieceCount > 0 && CompletedPieces == PieceCount;
        if (progress.Phase == TorrentSessionPhase.Seeding)
        {
            ResumeSeeding = true;
        }
        Status = progress.Phase == TorrentSessionPhase.Completed ? "Complete" : progress.Phase.ToString();
    }

    public void RefreshTransfer()
    {
        if (_session is not null)
        {
            if (_cancellation?.IsCancellationRequested != true &&
                _session.GetLatestProgress() is TorrentSessionProgress progress && progress.Timestamp > LastProgressAt)
            {
                ApplyProgress(progress);
            }

            int boundPort = _session.ListeningPort;
            if (boundPort != 0 && ActiveListenPort != boundPort)
            {
                ActiveListenPort = boundPort;
                OnPropertyChanged(nameof(ListeningPortText));
            }

            ApplyTransferSnapshot(_session.GetTransferSnapshot());
        }
    }

    public void ApplyTransferSnapshot(TorrentTransferSnapshot snapshot)
    {
        long received = Math.Min(long.MaxValue - _runBaseBytes, snapshot.PayloadBytesReceived) + _runBaseBytes;
        long verified = Math.Min(long.MaxValue - _runBaseVerifiedBytes, snapshot.VerifiedBytesFromPeers) + _runBaseVerifiedBytes;
        long uploaded = Math.Min(long.MaxValue - _runBaseUploadedBytes, snapshot.UploadedBytes) + _runBaseUploadedBytes;
        long ticks = Math.Min(long.MaxValue - _runBaseTicks, snapshot.ActiveTime.Ticks) + _runBaseTicks;
        ActiveUploadPeers = snapshot.ActiveUploadPeers;
        if (_payloadBytesReceived == received && _verifiedBytesFromPeers == verified &&
            _uploadedBytes == uploaded && _activeTicks == ticks && _lastRunContributors == snapshot.ContributingPeers)
        {
            return;
        }

        _payloadBytesReceived = received;
        _verifiedBytesFromPeers = verified;
        _uploadedBytes = uploaded;
        _activeTicks = ticks;
        _lastRunContributors = snapshot.ContributingPeers;
        NotifyTransferChanged();
    }

    private void NotifyTransferChanged()
    {
        OnPropertyChanged(nameof(NetworkReceivedText));
        OnPropertyChanged(nameof(VerifiedFromPeersText));
        OnPropertyChanged(nameof(UploadedText));
        OnPropertyChanged(nameof(QueueDetailText));
        OnPropertyChanged(nameof(ActiveAverageText));
        OnPropertyChanged(nameof(ActiveTimeText));
        OnPropertyChanged(nameof(ContributorText));
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
