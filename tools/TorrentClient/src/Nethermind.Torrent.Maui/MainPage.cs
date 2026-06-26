// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.ObjectModel;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace Nethermind.Torrent.Maui;

public sealed class MainPage : ContentPage
{
    private static readonly Color PageBackground = Color.FromArgb("#F8FAFC");
    private static readonly Color PanelBackground = Color.FromArgb("#FFFFFF");
    private static readonly Color MutedBackground = Color.FromArgb("#EEF2F7");
    private static readonly Color BorderColor = Color.FromArgb("#D7DEE8");
    private static readonly Color PrimaryColor = Color.FromArgb("#0F766E");
    private static readonly Color AccentColor = Color.FromArgb("#2563EB");
    private static readonly Color TextColor = Color.FromArgb("#111827");
    private static readonly Color MutedTextColor = Color.FromArgb("#64748B");

    private readonly ObservableCollection<TorrentJob> _jobs = [];
    private readonly TorrentUiSettings _settings = TorrentUiSettingsStore.Load();
    private readonly CollectionView _queueView;
    private readonly ContentView _detailContent = new();
    private readonly Label _statusLabel = SmallLabel("Ready");
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly Entry _outputEntry;
    private TorrentJob? _selectedJob;
    private string _activeTab = "Overview";

    internal static MainPage? Active { get; private set; }

    public MainPage()
    {
        Active = this;
        Title = "Nethermind Torrent";
        BackgroundColor = PageBackground;
        _outputEntry = new Entry { Text = _settings.DefaultDownloadDirectory, Placeholder = "Download directory" };
        _queueView = CreateQueueView();
        Content = BuildLayout();
        SetActiveTab("Overview");
        if (!string.IsNullOrWhiteSpace(TorrentUiSettingsStore.LastLoadError))
        {
            _statusLabel.Text = TorrentUiSettingsStore.LastLoadError;
        }
    }

    private Grid BuildLayout()
    {
        Grid root = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
        };

        root.Add(BuildToolbar(), 0, 0);
        root.Add(BuildBody(), 0, 1);
        root.Add(BuildStatusBar(), 0, 2);
        return root;
    }

    private View BuildToolbar()
    {
        Grid toolbar = new()
        {
            Padding = new Thickness(14, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            BackgroundColor = PanelBackground,
        };

        toolbar.Add(ToolButton("+ Add", AddTorrentAsync), 0, 0);
        toolbar.Add(ToolButton("Start", StartSelectedAsync), 1, 0);
        toolbar.Add(ToolButton("Pause", PauseSelectedAsync), 2, 0);
        toolbar.Add(ToolButton("Remove", RemoveSelectedAsync), 3, 0);
        toolbar.Add(ToolButton("Open Folder", OpenSelectedFolderAsync), 4, 0);

        _outputEntry.FontSize = 13;
        _outputEntry.HeightRequest = 38;
        _outputEntry.Unfocused += (_, _) =>
        {
            ApplyDownloadDirectoryFromToolbar();
            SaveSettings();
        };
        toolbar.Add(_outputEntry, 5, 0);
        toolbar.Add(ToolButton("Save Options", SaveOptionsAsync), 6, 0);

        return toolbar;
    }

    private View BuildBody()
    {
        Grid body = new()
        {
            Padding = new Thickness(12),
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(360)),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 12,
        };

        body.Add(Panel("Queue", _queueView), 0, 0);
        body.Add(BuildDetailsPanel(), 1, 0);
        return body;
    }

    private View BuildDetailsPanel()
    {
        Grid details = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 10,
        };

        details.Add(BuildTabStrip(), 0, 0);
        details.Add(Panel(null, _detailContent), 0, 1);
        return details;
    }

    private View BuildTabStrip()
    {
        HorizontalStackLayout tabs = new()
        {
            Spacing = 8,
        };

        string[] names = ["Overview", "Files", "Trackers", "Peers", "Options", "Log"];
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            Button button = new()
            {
                Text = name,
                CornerRadius = 6,
                HeightRequest = 38,
                Padding = new Thickness(14, 6),
            };
            button.Clicked += (_, _) => SetActiveTab(name);
            _tabButtons[name] = button;
            tabs.Add(button);
        }

        return tabs;
    }

    private View BuildStatusBar()
    {
        Grid bar = new()
        {
            Padding = new Thickness(12, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            BackgroundColor = Color.FromArgb("#E5EAF1"),
        };

        bar.Add(_statusLabel, 0, 0);
        bar.Add(SmallLabel("Engine: trackers, DHT, peer-wire, SHA-1 verification"), 1, 0);
        return bar;
    }

    private CollectionView CreateQueueView()
    {
        CollectionView view = new()
        {
            ItemsSource = _jobs,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = CreateQueueTemplate(),
        };
        view.SelectionChanged += (_, e) =>
        {
            _selectedJob = e.CurrentSelection.Count > 0 ? e.CurrentSelection[0] as TorrentJob : null;
            RefreshDetails();
        };
        return view;
    }

    private static DataTemplate CreateQueueTemplate()
        => new(() =>
        {
            Grid grid = new()
            {
                Padding = new Thickness(10),
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                },
                RowSpacing = 4,
                ColumnSpacing = 8,
            };

            Label name = new() { FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = TextColor, LineBreakMode = LineBreakMode.TailTruncation };
            name.SetBinding(Label.TextProperty, nameof(TorrentJob.Name));
            Label status = SmallLabel(string.Empty);
            status.SetBinding(Label.TextProperty, nameof(TorrentJob.Status));
            ProgressBar progress = BoundProgressBar(7);
            Label progressText = SmallLabel(string.Empty);
            progressText.SetBinding(Label.TextProperty, nameof(TorrentJob.ProgressText));
            Label speed = SmallLabel(string.Empty);
            speed.SetBinding(Label.TextProperty, nameof(TorrentJob.DownloadRateText));
            Label message = SmallLabel(string.Empty);
            message.LineBreakMode = LineBreakMode.TailTruncation;
            message.SetBinding(Label.TextProperty, nameof(TorrentJob.Message));

            grid.Add(name, 0, 0);
            grid.Add(status, 1, 0);
            grid.Add(progress, 0, 1);
            Grid.SetColumnSpan(progress, 2);
            grid.Add(progressText, 0, 2);
            grid.Add(speed, 1, 2);
            grid.Add(message, 0, 3);
            Grid.SetColumnSpan(message, 2);

            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Stroke = BorderColor,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                BackgroundColor = PanelBackground,
                Content = grid,
            };
        });

    private void SetActiveTab(string name)
    {
        _activeTab = name;
        foreach ((string tabName, Button button) in _tabButtons)
        {
            bool selected = string.Equals(tabName, name, StringComparison.Ordinal);
            button.BackgroundColor = selected ? PrimaryColor : MutedBackground;
            button.TextColor = selected ? Colors.White : TextColor;
        }

        RefreshDetails();
    }

    private void RefreshDetails() =>
        _detailContent.Content = _activeTab switch
        {
            "Overview" => BuildOverviewPanel(_selectedJob),
            "Files" => BuildFilesPanel(_selectedJob),
            "Trackers" => BuildTrackersPanel(_selectedJob),
            "Peers" => BuildPeersPanel(_selectedJob),
            "Options" => BuildOptionsPanel(),
            "Log" => BuildLogPanel(_selectedJob),
            _ => BuildOverviewPanel(_selectedJob),
        };

    private View BuildOverviewPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("Add a torrent to begin.");
        }

        Grid grid = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            RowSpacing = 12,
            ColumnSpacing = 12,
            Padding = new Thickness(4),
            BindingContext = job,
        };

        Label title = new() { FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = TextColor };
        title.SetBinding(Label.TextProperty, nameof(TorrentJob.Name));
        grid.Add(title, 0, 0);
        Grid.SetColumnSpan(title, 2);

        ProgressBar progress = BoundProgressBar(10);
        grid.Add(progress, 0, 1);
        Grid.SetColumnSpan(progress, 2);

        grid.Add(InfoPanel("Transfer", [
            BoundInfo("Status", nameof(TorrentJob.Status)),
            BoundInfo("Progress", nameof(TorrentJob.ProgressText)),
            BoundInfo("Downloaded", nameof(TorrentJob.DownloadedText)),
            BoundInfo("Remaining", nameof(TorrentJob.RemainingText)),
            BoundInfo("Down rate", nameof(TorrentJob.DownloadRateText)),
        ]), 0, 2);

        grid.Add(InfoPanel("Swarm", [
            BoundInfo("Pieces", nameof(TorrentJob.PiecesText)),
            BoundInfo("Active peers", nameof(TorrentJob.ActivePeers)),
            BoundInfo("Known peers", nameof(TorrentJob.KnownPeers)),
            BoundInfo("Phase", nameof(TorrentJob.Phase)),
            BoundInfo("Message", nameof(TorrentJob.Message)),
        ]), 1, 2);

        return new ScrollView { Content = grid };
    }

    private View BuildFilesPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid(job);
        CollectionView files = new()
        {
            ItemsSource = job.Files,
            ItemTemplate = new DataTemplate(() =>
            {
                Grid row = new()
                {
                    Padding = new Thickness(8, 6),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(110)),
                        new ColumnDefinition(new GridLength(100)),
                    },
                    ColumnSpacing = 10,
                };
                Label path = SmallLabel(string.Empty);
                path.TextColor = TextColor;
                path.SetBinding(Label.TextProperty, nameof(TorrentFileItem.Path));
                Label length = SmallLabel(string.Empty);
                length.SetBinding(Label.TextProperty, nameof(TorrentFileItem.LengthText));
                Label priority = SmallLabel("Normal");
                priority.TextColor = MutedTextColor;
                priority.HorizontalTextAlignment = TextAlignment.End;
                priority.VerticalOptions = LayoutOptions.Center;
                row.Add(path, 0, 0);
                row.Add(length, 1, 0);
                row.Add(priority, 2, 0);
                return row;
            }),
        };

        grid.Add(files, 0, 1);
        return grid;
    }

    private View BuildTrackersPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid(job);
        CollectionView trackers = new()
        {
            ItemsSource = job.Trackers,
            ItemTemplate = new DataTemplate(() =>
            {
                Label label = SmallLabel(string.Empty);
                label.Padding = new Thickness(8, 7);
                label.TextColor = TextColor;
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };

        grid.Add(trackers, 0, 1);
        return grid;
    }

    private View BuildPeersPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 10,
        };
        grid.Add(BuildProgressHeader(job), 0, 0);
        grid.Add(InfoPanel("Peer Summary", [
            BoundInfo("Active peers", nameof(TorrentJob.ActivePeers)),
            BoundInfo("Known peers", nameof(TorrentJob.KnownPeers)),
            BoundInfo("DHT", nameof(TorrentJob.EffectiveDhtText)),
            BoundInfo("Trackers", nameof(TorrentJob.EffectiveTrackersText)),
        ], job), 0, 1);

        CollectionView eventsView = new()
        {
            ItemsSource = job.PeerEvents,
            ItemTemplate = new DataTemplate(() =>
            {
                Label label = SmallLabel(string.Empty);
                label.Padding = new Thickness(8, 5);
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };
        grid.Add(eventsView, 0, 2);
        return grid;
    }

    private View BuildLogPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid(job);
        CollectionView log = new()
        {
            ItemsSource = job.LogLines,
            ItemTemplate = new DataTemplate(() =>
            {
                Label label = SmallLabel(string.Empty);
                label.FontFamily = "Consolas";
                label.FontSize = 12;
                label.LineBreakMode = LineBreakMode.TailTruncation;
                label.Padding = new Thickness(8, 4);
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };
        grid.Add(log, 0, 1);
        return grid;
    }

    private static Grid TabContentGrid(TorrentJob job)
    {
        Grid grid = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 10,
        };
        grid.Add(BuildProgressHeader(job), 0, 0);
        return grid;
    }

    private static View BuildProgressHeader(TorrentJob job)
    {
        Grid grid = new()
        {
            Padding = new Thickness(10, 8),
            BindingContext = job,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
            },
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            RowSpacing = 5,
            ColumnSpacing = 12,
        };

        Label name = new() { FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = TextColor, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(TorrentJob.Name));
        Label percent = SmallLabel(string.Empty);
        percent.TextColor = TextColor;
        percent.SetBinding(Label.TextProperty, nameof(TorrentJob.ProgressText));
        Label rate = SmallLabel(string.Empty);
        rate.SetBinding(Label.TextProperty, nameof(TorrentJob.DownloadRateText));

        ProgressBar progress = BoundProgressBar(8);
        Label downloaded = SmallLabel(string.Empty);
        downloaded.SetBinding(Label.TextProperty, nameof(TorrentJob.DownloadedText), stringFormat: "Downloaded {0}");
        Label remaining = SmallLabel(string.Empty);
        remaining.SetBinding(Label.TextProperty, nameof(TorrentJob.RemainingText), stringFormat: "Remaining {0}");
        Label pieces = SmallLabel(string.Empty);
        pieces.SetBinding(Label.TextProperty, nameof(TorrentJob.PiecesText), stringFormat: "Pieces {0}");

        grid.Add(name, 0, 0);
        grid.Add(percent, 1, 0);
        grid.Add(rate, 2, 0);
        grid.Add(progress, 0, 1);
        Grid.SetColumnSpan(progress, 3);
        grid.Add(downloaded, 0, 2);
        grid.Add(remaining, 1, 2);
        grid.Add(pieces, 2, 2);

        return new Border
        {
            Stroke = BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#FBFCFE"),
            Content = grid,
        };
    }

    private static ProgressBar BoundProgressBar(double height)
    {
        ProgressBar progress = new()
        {
            HeightRequest = height,
            BackgroundColor = MutedBackground,
            ProgressColor = PrimaryColor,
        };
        progress.SetBinding(ProgressBar.ProgressProperty, nameof(TorrentJob.Progress));
        return progress;
    }

    private View BuildOptionsPanel()
    {
        VerticalStackLayout stack = new()
        {
            Padding = new Thickness(4),
            Spacing = 12,
        };

        stack.Add(SettingsSection("Storage", [
            TextSetting("Default download directory", _settings.DefaultDownloadDirectory, SetDefaultDownloadDirectory),
            ReadOnlySetting("Incomplete directory", "Not available"),
            ReadOnlySetting("Completed directory", "Not available"),
            ReadOnlySetting("Watch directory", "Not available"),
            SwitchSetting("Start torrents after adding", _settings.StartOnAdd, value => _settings.StartOnAdd = value),
            SwitchSetting("Add new torrents paused", _settings.AddPaused, value => _settings.AddPaused = value),
            SwitchSetting("Verify existing data before download", _settings.VerifyExistingData, value => _settings.VerifyExistingData = value),
            ReadOnlySetting("Pre-allocate files", "Always"),
            ReadOnlySetting("Append incomplete extension", "Not available"),
        ]));

        stack.Add(SettingsSection("Connection", [
            NumberSetting("Listen port", _settings.ListenPort, value => _settings.ListenPort = value, 1, 65535),
            SwitchSetting("Randomize port on start", _settings.RandomizePortOnStart, value => _settings.RandomizePortOnStart = value),
            NumberSetting("Peers per torrent", _settings.MaxPeersPerTorrent, value => _settings.MaxPeersPerTorrent = value, 1, 512),
            ReadOnlySetting("Global peer limit", "Single-torrent queue"),
            ReadOnlySetting("Upload slots per torrent", "Not available"),
            ReadOnlySetting("IPv6 peers", "Automatic"),
            ReadOnlySetting("UPnP port mapping", "Not available"),
            ReadOnlySetting("NAT-PMP port mapping", "Not available"),
        ]));

        stack.Add(SettingsSection("BitTorrent", [
            SwitchSetting("Use trackers", _settings.EnableTrackers, value => _settings.EnableTrackers = value),
            SwitchSetting("Use DHT", _settings.EnableDht, value => _settings.EnableDht = value),
            ReadOnlySetting("Peer exchange", "Not available"),
            ReadOnlySetting("Local peer discovery", "Not available"),
            ReadOnlySetting("uTP transport", "Not available"),
            ReadOnlySetting("TCP transport", "Enabled"),
            ReadOnlySetting("Anonymous mode", "Not available"),
            ReadOnlySetting("Require encrypted peers", "Not available"),
            ReadOnlySetting("Allow unencrypted peers", "Enabled"),
        ]));

        stack.Add(SettingsSection("Bandwidth", [
            ReadOnlySetting("Download limit KiB/s", "Unlimited"),
            ReadOnlySetting("Upload limit KiB/s", "Not available"),
            ReadOnlySetting("Use alternative limits", "Not available"),
            ReadOnlySetting("Alternative download KiB/s", "Not available"),
            ReadOnlySetting("Alternative upload KiB/s", "Not available"),
        ]));

        stack.Add(SettingsSection("Queue and Seeding", [
            ReadOnlySetting("Active downloads", "Manual"),
            ReadOnlySetting("Active seeds", "Not available"),
            ReadOnlySetting("Active torrents", "Manual"),
            ReadOnlySetting("Ratio limit", "Not available"),
            ReadOnlySetting("Seed time minutes", "Not available"),
            ReadOnlySetting("Stop when complete", "Always"),
        ]));

        stack.Add(SettingsSection("Proxy and Advanced", [
            ReadOnlySetting("Proxy host", "Not available"),
            ReadOnlySetting("Proxy port", "Not available"),
            ReadOnlySetting("Proxy trackers only", "Not available"),
            NumberSetting("Tracker timeout seconds", _settings.TrackerTimeoutSeconds, value => _settings.TrackerTimeoutSeconds = value, 1, 3600),
            NumberSetting("DHT interval seconds", _settings.DhtLookupIntervalSeconds, value => _settings.DhtLookupIntervalSeconds = value, 1, 3600),
            NumberSetting("DHT timeout seconds", _settings.DhtLookupTimeoutSeconds, value => _settings.DhtLookupTimeoutSeconds = value, 1, 3600),
            NumberSetting("Peer timeout seconds", _settings.PeerTimeoutSeconds, value => _settings.PeerTimeoutSeconds = value, 1, 3600),
            ReadOnlySetting("Piece cache MiB", "Automatic"),
            ReadOnlySetting("Disk write buffer KiB", "1024"),
            ReadOnlySetting("Show notifications", "Not available"),
            SwitchSetting("Confirm remove", _settings.ConfirmRemove, value => _settings.ConfirmRemove = value),
        ]));

        return new ScrollView { Content = stack };
    }

    private async Task AddTorrentAsync()
    {
        try
        {
            FilePickerFileType torrentType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = [".torrent"],
            });
            PickOptions options = new()
            {
                PickerTitle = "Open torrent file",
                FileTypes = torrentType,
            };
            FileResult? result = await FilePicker.Default.PickAsync(options);
            if (result is null || string.IsNullOrWhiteSpace(result.FullPath))
            {
                return;
            }

            ApplyDownloadDirectoryFromToolbar();
            SaveSettings();

            TorrentJob job = new(result.FullPath, _settings.DefaultDownloadDirectory);
            LoadMetadata(job);
            job.PropertyChanged += (_, _) =>
            {
                if (ReferenceEquals(job, _selectedJob))
                {
                    MainThread.BeginInvokeOnMainThread(() => _statusLabel.Text = $"{job.Name}: {job.Status} - {job.ProgressText}");
                }
            };
            _jobs.Add(job);
            _queueView.SelectedItem = job;
            _statusLabel.Text = $"Added {job.Name}";

            if (_settings.StartOnAdd && !_settings.AddPaused)
            {
                await StartJobAsync(job);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Add torrent failed", exception.Message, "OK");
        }
    }

    private void LoadMetadata(TorrentJob job)
    {
        TorrentMetadata metadata = TorrentMetadata.Load(job.TorrentPath);
        job.ApplyMetadata(metadata);
        job.Status = "Ready";
        job.Message = $"{metadata.PieceCount} pieces, {metadata.Trackers.Count} trackers";
    }

    private async Task StartSelectedAsync()
    {
        if (_selectedJob is not null)
        {
            await StartJobAsync(_selectedJob);
        }
    }

    private async Task StartJobAsync(TorrentJob job)
    {
        if (job.IsRunning || job.IsComplete)
        {
            return;
        }

        try
        {
            if (!_settings.EnableDht && !_settings.EnableTrackers)
            {
                await DisplayAlertAsync("Discovery disabled", "Enable trackers, DHT, or both before starting.", "OK");
                return;
            }

            TorrentClientOptions options = _settings.ToClientOptions(job.TorrentPath, job.OutputDirectory);
            job.ApplyEffectiveOptions(options);
            CancellationTokenSource cancellation = new();
            Progress<TorrentSessionProgress> progress = new(snapshot =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    job.ApplyProgress(snapshot);
                    _statusLabel.Text = $"{job.Name}: {snapshot.Message}";
                });
            });
            TorrentSession session = new(options, line => MainThread.BeginInvokeOnMainThread(() => job.AppendLog(line)), progress);
            Task runTask = Task.Run(async () =>
            {
                bool completed = false;
                try
                {
                    _ = await session.RunAsync(cancellation.Token);
                    completed = true;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        job.Status = "Complete";
                        job.Message = "Completed";
                    });
                }
                catch (OperationCanceledException)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        job.Status = "Paused";
                        job.Message = "Paused";
                    });
                }
                catch (Exception exception)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        job.Status = "Error";
                        job.Message = exception.Message;
                        job.AppendLog("error: " + exception.Message);
                    });
                }
                finally
                {
                    MainThread.BeginInvokeOnMainThread(() => job.DetachRun(completed));
                }
            });

            job.AttachRun(runTask, cancellation);
            job.Status = "Starting";
            job.Message = "Starting session";
            _statusLabel.Text = $"Starting {job.Name}";
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Start failed", exception.Message, "OK");
        }
    }

    private async Task PauseSelectedAsync()
    {
        if (_selectedJob is null)
        {
            return;
        }

        await _selectedJob.StopAsync();
        _statusLabel.Text = $"Paused {_selectedJob.Name}";
    }

    private async Task RemoveSelectedAsync()
    {
        TorrentJob? job = _selectedJob;
        if (job is null)
        {
            return;
        }

        if (_settings.ConfirmRemove && !await DisplayAlertAsync("Remove torrent", $"Remove {job.Name} from the queue?", "Remove", "Cancel"))
        {
            return;
        }

        await job.StopAsync();
        _jobs.Remove(job);
        _selectedJob = null;
        RefreshDetails();
        _statusLabel.Text = $"Removed {job.Name}";
    }

    private async Task OpenSelectedFolderAsync()
    {
        if (_selectedJob is null)
        {
            return;
        }

        string path = Path.GetFullPath(_selectedJob.OutputDirectory);
        Directory.CreateDirectory(path);
        System.Diagnostics.ProcessStartInfo startInfo = new("explorer.exe", "\"" + path + "\"")
        {
            UseShellExecute = false,
        };
        System.Diagnostics.Process.Start(startInfo);
        await Task.CompletedTask;
    }

    private Task SaveOptionsAsync()
    {
        ApplyDownloadDirectoryFromToolbar();
        if (SaveSettings())
        {
            _statusLabel.Text = "Options saved";
        }

        return Task.CompletedTask;
    }

    internal async Task StopAllAsync(TimeSpan timeout)
    {
        List<Task> stops = CreateStopTasks();
        if (stops.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(stops).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            MainThread.BeginInvokeOnMainThread(() => _statusLabel.Text = "Timed out while stopping active torrents");
        }
    }

    internal void StopAllForShutdown(TimeSpan timeout)
    {
        List<Task> stops = CreateStopTasks();
        if (stops.Count == 0)
        {
            return;
        }

        try
        {
            _ = Task.WhenAll(stops).Wait(timeout);
        }
        catch (AggregateException exception)
        {
            _statusLabel.Text = "Shutdown stop failed: " + exception.GetBaseException().Message;
        }
    }

    private List<Task> CreateStopTasks()
    {
        List<Task> stops = [];
        for (int i = 0; i < _jobs.Count; i++)
        {
            if (_jobs[i].IsRunning)
            {
                stops.Add(_jobs[i].StopAsync());
            }
        }

        return stops;
    }

    private bool SaveSettings()
    {
        try
        {
            TorrentUiSettingsStore.Save(_settings);
            return true;
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Settings save failed: " + exception.Message;
            return false;
        }
    }

    private void ApplyDownloadDirectoryFromToolbar()
    {
        if (!string.IsNullOrWhiteSpace(_outputEntry.Text))
        {
            _settings.DefaultDownloadDirectory = _outputEntry.Text;
        }
    }

    private void SetDefaultDownloadDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settings.DefaultDownloadDirectory = value;
        if (!string.Equals(_outputEntry.Text, value, StringComparison.Ordinal))
        {
            _outputEntry.Text = value;
        }
    }

    private Button ToolButton(string text, Func<Task> action)
    {
        Button button = new()
        {
            Text = text,
            CornerRadius = 6,
            HeightRequest = 38,
            Padding = new Thickness(14, 6),
            BackgroundColor = PrimaryColor,
            TextColor = Colors.White,
        };
        button.Clicked += (_, _) => _ = RunUiActionAsync(action);
        return button;
    }

    private async Task RunUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Action failed", exception.Message, "OK");
        }
    }

    private View Panel(string? title, View content)
    {
        Grid grid = new()
        {
            RowDefinitions =
            {
                title is null ? new RowDefinition(new GridLength(0)) : new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
        };

        if (title is not null)
        {
            Label label = new()
            {
                Text = title,
                FontAttributes = FontAttributes.Bold,
                FontSize = 15,
                TextColor = TextColor,
                Margin = new Thickness(12, 10, 12, 8),
            };
            grid.Add(label, 0, 0);
        }

        grid.Add(content, 0, 1);
        return new Border
        {
            Stroke = BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = PanelBackground,
            Content = grid,
        };
    }

    private static View EmptyState(string text)
        => new Grid
        {
            Children =
            {
                new Label
                {
                    Text = text,
                    TextColor = MutedTextColor,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                },
            },
        };

    private static Label SmallLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 12,
            TextColor = MutedTextColor,
            VerticalTextAlignment = TextAlignment.Center,
        };

    private static View InfoPanel(string title, IReadOnlyList<View> rows, object? bindingContext = null)
    {
        VerticalStackLayout stack = new()
        {
            Spacing = 7,
            Padding = new Thickness(12),
        };
        if (bindingContext is not null)
        {
            stack.BindingContext = bindingContext;
        }

        stack.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, TextColor = TextColor, FontSize = 15 });
        for (int i = 0; i < rows.Count; i++)
        {
            stack.Add(rows[i]);
        }

        return new Border
        {
            Stroke = BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#FBFCFE"),
            Content = stack,
        };
    }

    private static View BoundInfo(string label, string bindingPath)
    {
        Grid grid = InfoRowBase(label);
        Label value = SmallLabel(string.Empty);
        value.TextColor = TextColor;
        value.HorizontalTextAlignment = TextAlignment.End;
        value.SetBinding(Label.TextProperty, bindingPath);
        grid.Add(value, 1, 0);
        return grid;
    }

    private static View BoundInfo(string label, object value)
    {
        Grid grid = InfoRowBase(label);
        Label valueLabel = SmallLabel(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        valueLabel.TextColor = TextColor;
        valueLabel.HorizontalTextAlignment = TextAlignment.End;
        grid.Add(valueLabel, 1, 0);
        return grid;
    }

    private static Grid InfoRowBase(string label)
        => new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            Children =
            {
                SmallLabel(label),
            },
        };

    private View SettingsSection(string title, IReadOnlyList<View> rows)
    {
        VerticalStackLayout stack = new()
        {
            Padding = new Thickness(12),
            Spacing = 7,
        };
        stack.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = TextColor });
        for (int i = 0; i < rows.Count; i++)
        {
            stack.Add(rows[i]);
        }

        return new Border
        {
            Stroke = BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#FBFCFE"),
            Content = stack,
        };
    }

    private View SwitchSetting(string label, bool initial, Action<bool> update)
    {
        Grid row = SettingRowBase(label);
        Microsoft.Maui.Controls.Switch control = new()
        {
            IsToggled = initial,
            HorizontalOptions = LayoutOptions.End,
        };
        control.Toggled += (_, e) =>
        {
            update(e.Value);
            SaveSettings();
        };
        row.Add(control, 1, 0);
        return row;
    }

    private static View ReadOnlySetting(string label, object value)
    {
        Grid row = SettingRowBase(label);
        Label valueLabel = SmallLabel(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        valueLabel.TextColor = MutedTextColor;
        valueLabel.HorizontalTextAlignment = TextAlignment.End;
        row.Add(valueLabel, 1, 0);
        return row;
    }

    private View TextSetting(string label, string initial, Action<string> update)
    {
        Grid row = SettingRowBase(label);
        Entry entry = new()
        {
            Text = initial,
            HeightRequest = 36,
            FontSize = 13,
        };
        void Apply()
        {
            update(entry.Text ?? string.Empty);
            SaveSettings();
        }

        entry.Completed += (_, _) => Apply();
        entry.Unfocused += (_, _) => Apply();
        row.Add(entry, 1, 0);
        return row;
    }

    private View NumberSetting(string label, int initial, Action<int> update, int minimum, int maximum)
    {
        Grid row = SettingRowBase(label);
        Entry entry = new()
        {
            Text = initial.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Keyboard = Keyboard.Numeric,
            HeightRequest = 36,
            FontSize = 13,
            HorizontalTextAlignment = TextAlignment.End,
        };
        void Apply()
        {
            if (int.TryParse(entry.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                value = Math.Clamp(value, minimum, maximum);
                entry.Text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                update(value);
                SaveSettings();
            }
        }

        entry.Completed += (_, _) => Apply();
        entry.Unfocused += (_, _) => Apply();
        row.Add(entry, 1, 0);
        return row;
    }

    private static Grid SettingRowBase(string label)
        => new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(260)),
            },
            ColumnSpacing = 12,
            Children =
            {
                new Label
                {
                    Text = label,
                    FontSize = 13,
                    TextColor = TextColor,
                    VerticalTextAlignment = TextAlignment.Center,
                },
            },
        };
}
