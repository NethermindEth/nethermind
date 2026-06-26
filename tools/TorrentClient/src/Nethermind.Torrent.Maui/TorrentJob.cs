// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nethermind.Torrent.Maui;

internal sealed class TorrentJob : INotifyPropertyChanged
{
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
    private double _progress;
    private DateTimeOffset _lastProgressAt = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private long _lastDownloadedBytes;
    private DateTimeOffset _lastSpeedSampleAt = DateTimeOffset.UtcNow;
    private double _downloadRateBytesPerSecond;
    private bool? _effectiveDht;
    private bool? _effectiveTrackers;

    public TorrentJob(string torrentPath, string outputDirectory)
    {
        TorrentPath = torrentPath;
        _outputDirectory = outputDirectory;
        Name = Path.GetFileNameWithoutExtension(torrentPath);
    }

    public string TorrentPath { get; }

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

    public ObservableCollection<string> PeerEvents { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public string TotalText => FormatBytes(TotalBytes);

    public string DownloadedText => FormatBytes(DownloadedBytes);

    public string RemainingText => FormatBytes(Math.Max(0, TotalBytes - DownloadedBytes));

    public string PiecesText => $"{CompletedPieces}/{PieceCount}";

    public string ProgressText => (Progress * 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";

    public string DownloadRateText => FormatBytes((long)DownloadRateBytesPerSecond) + "/s";

    public string EffectiveDhtText => FormatEnabled(_effectiveDht);

    public string EffectiveTrackersText => FormatEnabled(_effectiveTrackers);

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool CanStart => !IsRunning && !IsComplete;

    public bool CanStop => IsRunning;

    public void ApplyMetadata(TorrentMetadata metadata)
    {
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
    }

    public void AttachRun(Task runTask, CancellationTokenSource cancellation)
    {
        _runTask = runTask;
        _cancellation = cancellation;
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
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public void AppendLog(string line)
    {
        if (LogLines.Count > 800)
        {
            LogLines.RemoveAt(0);
        }

        LogLines.Add($"{DateTimeOffset.Now:HH:mm:ss}  {line}");
        if (line.StartsWith("peer ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("dht ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains(" peers", StringComparison.OrdinalIgnoreCase))
        {
            if (PeerEvents.Count > 120)
            {
                PeerEvents.RemoveAt(0);
            }

            PeerEvents.Add(line);
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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
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

    public string LengthText { get; } = FormatBytes(entry.Length);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
