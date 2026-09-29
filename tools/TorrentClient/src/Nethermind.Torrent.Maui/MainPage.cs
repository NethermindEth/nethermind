// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.ObjectModel;
using Microsoft.Maui.Dispatching;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace Nethermind.Torrent.Maui;

public sealed class MainPage : ContentPage
{
    private static readonly Color PageBackground = Color.FromArgb("#F6F8FA");
    private static readonly Color PanelBackground = Color.FromArgb("#FFFFFF");
    private static readonly Color MutedBackground = Color.FromArgb("#EEF2F4");
    private static readonly Color BorderColor = Color.FromArgb("#DCE3E8");
    private static readonly Color PrimaryColor = Color.FromArgb("#087F6D");
    private static readonly Color TextColor = Color.FromArgb("#18232D");
    private static readonly Color MutedTextColor = Color.FromArgb("#526371");
    private static readonly Color ErrorColor = Color.FromArgb("#B43D45");

    private readonly ObservableCollection<TorrentJob> _jobs = [];
    private readonly ObservableCollection<TorrentJob> _visibleJobs = [];
    private readonly List<(TorrentJob Job, TorrentMetadata Metadata)> _pendingVerifications = [];
    private readonly Dictionary<TorrentJob, (CancellationTokenSource Cancellation, Task Task)> _verifications = [];
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private readonly TorrentUiSettings _settings = TorrentUiSettingsStore.Load();
    private readonly CollectionView _queueView;
    private readonly ContentView _detailContent = new();
    private readonly Label _statusLabel = SmallLabel("Ready");
    private readonly Label _queueCountLabel = SmallLabel("0 torrents");
    private readonly Label _summaryLabel = SmallLabel("0 active");
    private readonly Label _downloadRateLabel = SmallLabel("Down 0 B/s  \u00b7  0 peers");
    private readonly Label _detailTitle = new() { FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = TextColor, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _detailSubtitle = SmallLabel(string.Empty);
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly Entry _searchEntry = new()
    {
        Placeholder = "Search torrents",
        FontSize = 13,
        HeightRequest = 36,
        TextColor = TextColor,
        PlaceholderColor = MutedTextColor,
    };
    private readonly Dictionary<string, Button> _filterButtons = [];
    private readonly Button _startButton;
    private readonly Button _pauseButton;
    private readonly Button _removeButton;
    private readonly Button _folderButton;
    private readonly Button _copyMagnetButton;
    private readonly Button _addButton;
    private readonly Button _pasteButton;
    private readonly ActivityIndicator _importIndicator = new() { Color = PrimaryColor, IsVisible = false, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center };
    private readonly ContentView _progressContent = new();
    private Button _settingsButton = null!;
    private Button _backButton = null!;
    private View _queuePane = null!;
    private View _detailsPane = null!;
    private HorizontalStackLayout _tabStrip = null!;
    private Grid _bodyGrid = null!;
    private TorrentJob? _selectedJob;
    private TorrentJob? _progressJob;
    private string _activeTab = "Overview";
    private string _queueFilter = "All";
    private bool _showSettings;
    private bool _queueBeforeSettings;
    private bool _isCompact;
    private bool _showQueueOnCompact = true;
    private bool _filteringJobs;
    private bool _queueStorageAvailable = true;
    private IDispatcherTimer? _activityTimer;
    private CancellationTokenSource? _magnetImportCancellation;
    private Task<byte[]>? _magnetResolutionTask;
    private bool _importPending;

    internal static MainPage? Active { get; private set; }

    public MainPage()
    {
        Active = this;
        Title = "Nethermind Torrent";
        BackgroundColor = PageBackground;
        Shell.SetNavBarIsVisible(this, false);
        _queueView = CreateQueueView();
        _startButton = ToolButton("Start", "\uE768", StartSelectedAsync);
        _pauseButton = ToolButton("Pause", "\uE769", PauseSelectedAsync);
        _folderButton = ToolButton("Open folder", "\uE8B7", OpenSelectedFolderAsync);
        _copyMagnetButton = ToolButton("Copy magnet link", "\uE71B", CopyMagnetAsync);
        _removeButton = ToolButton("Remove", "\uE74D", RemoveSelectedAsync);
        _addButton = ToolButton("Add torrent", "\uE710", AddTorrentAsync, primary: true);
        _pasteButton = ToolButton("Paste link", "\uE8C8", PasteLinkAsync);
        Content = BuildLayout();
        RestoreQueue();
        SetQueueFilter("All");
        SetActiveTab("Overview");
        RefreshActionState();
        SizeChanged += (_, _) => UpdateAdaptiveLayout();
        Loaded += (_, _) =>
        {
            StartActivityTimer();
            foreach ((TorrentJob job, TorrentMetadata metadata) in _pendingVerifications)
            {
                QueueVerification(job, metadata);
            }

            _pendingVerifications.Clear();
        };
        Unloaded += (_, _) =>
        {
            _activityTimer?.Stop();
            CancelAllVerifications();
            _magnetImportCancellation?.Cancel();
        };
        if (!string.IsNullOrWhiteSpace(TorrentUiSettingsStore.LastLoadError))
        {
            _statusLabel.Text = string.IsNullOrWhiteSpace(_statusLabel.Text) || _statusLabel.Text == "Ready"
                ? TorrentUiSettingsStore.LastLoadError
                : _statusLabel.Text + "; " + TorrentUiSettingsStore.LastLoadError;
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
            Padding = new Thickness(16, 8),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 8,
            BackgroundColor = PanelBackground,
        };

        HorizontalStackLayout commands = new() { Spacing = 4 };
        commands.Add(_addButton);
        commands.Add(_pasteButton);
        commands.Add(_startButton);
        commands.Add(_pauseButton);
        commands.Add(_folderButton);
        commands.Add(_copyMagnetButton);
        commands.Add(_removeButton);
        toolbar.Add(commands, 0, 0);
        toolbar.Add(_importIndicator, 1, 0);
        toolbar.Add(_summaryLabel, 2, 0);
        _settingsButton = ToolButton("Settings", "\uE713", ShowSettingsAsync);
        toolbar.Add(_settingsButton, 3, 0);

        return toolbar;
    }

    private View BuildBody()
    {
        _bodyGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(336)),
                new ColumnDefinition(GridLength.Star),
            },
            BackgroundColor = PanelBackground,
        };

        _queuePane = BuildQueuePanel();
        _detailsPane = BuildDetailsPanel();
        _bodyGrid.Add(_queuePane, 0, 0);
        _bodyGrid.Add(_detailsPane, 1, 0);
        return _bodyGrid;
    }

    private View BuildQueuePanel()
    {
        Grid queue = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            Padding = new Thickness(16, 12, 16, 0),
            RowSpacing = 10,
        };

        Grid heading = new()
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        heading.Add(new Label { Text = "Queue", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = TextColor }, 0, 0);
        heading.Add(_queueCountLabel, 1, 0);
        queue.Add(heading, 0, 0);

        _searchEntry.TextChanged += (_, _) => FilterJobs();
        queue.Add(_searchEntry, 0, 1);

        HorizontalStackLayout filters = new() { Spacing = 4 };
        foreach (string filter in new[] { "All", "Active", "Done" })
        {
            Button button = new()
            {
                Text = filter,
                HeightRequest = 32,
                MinimumHeightRequest = 32,
                Padding = new Thickness(12, 2),
                CornerRadius = 4,
                FontSize = 12,
            };
            button.Clicked += (_, _) => SetQueueFilter(filter);
            _filterButtons[filter] = button;
            filters.Add(button);
        }
        queue.Add(filters, 0, 2);
        queue.Add(_queueView, 0, 3);

        Grid frame = new()
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(1)) },
            BackgroundColor = PageBackground,
        };
        frame.Add(queue, 0, 0);
        frame.Add(new BoxView { BackgroundColor = BorderColor }, 1, 0);
        return frame;
    }

    private View BuildDetailsPanel()
    {
        Grid details = new()
        {
            Padding = new Thickness(24, 12, 24, 12),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 10,
        };

        Grid heading = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 10,
        };
        _backButton = ToolButton("Back to queue", "\uE72B", BackToQueueAsync);
        _backButton.IsVisible = false;
        heading.Add(_backButton, 0, 0);
        VerticalStackLayout title = new() { Spacing = 2 };
        title.Add(_detailTitle);
        title.Add(_detailSubtitle);
        heading.Add(title, 1, 0);
        details.Add(heading, 0, 0);

        details.Add(_progressContent, 0, 1);
        _tabStrip = BuildTabStrip();
        details.Add(_tabStrip, 0, 2);
        details.Add(_detailContent, 0, 3);
        return details;
    }

    private HorizontalStackLayout BuildTabStrip()
    {
        HorizontalStackLayout tabs = new()
        {
            Spacing = 4,
        };

        string[] names = ["Overview", "Files", "Trackers", "Peers", "Activity"];
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            Button button = new()
            {
                Text = name,
                CornerRadius = 4,
                HeightRequest = 34,
                MinimumHeightRequest = 34,
                Padding = new Thickness(12, 4),
                FontSize = 13,
            };
            button.Clicked += (_, _) => SetActiveTab(name);
            _tabButtons[name] = button;
            tabs.Add(button);
        }

        return tabs;
    }

    private View BuildStatusBar()
    {
        _statusLabel.LineBreakMode = LineBreakMode.TailTruncation;
        Grid bar = new()
        {
            Padding = new Thickness(18, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            BackgroundColor = MutedBackground,
        };

        bar.Add(_statusLabel, 0, 0);
        bar.Add(_downloadRateLabel, 1, 0);
        return bar;
    }

    private CollectionView CreateQueueView()
    {
        CollectionView view = new()
        {
            ItemsSource = _visibleJobs,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = CreateQueueTemplate(),
            EmptyView = EmptyState("Queue is empty"),
        };
        view.SelectionChanged += (_, e) =>
        {
            if (_filteringJobs) return;

            SelectJob(e.CurrentSelection.Count > 0 ? e.CurrentSelection[0] as TorrentJob : null);
        };
        return view;
    }

    private void SelectJob(TorrentJob? job)
    {
        _selectedJob = job;
        if (job is not null)
        {
            _showSettings = false;
            _showQueueOnCompact = false;
        }

        RefreshDetails();
        RefreshActionState();
        UpdateAdaptiveLayout();
    }

    private DataTemplate CreateQueueTemplate()
        => new(() =>
        {
            Grid grid = new()
            {
                Padding = new Thickness(10, 12),
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

            Border item = new()
            {
                Margin = new Thickness(0, 0, 0, 1),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 4 },
                BackgroundColor = PanelBackground,
                Content = grid,
            };
            TapGestureRecognizer tap = new();
            tap.Tapped += (_, _) =>
            {
                if (item.BindingContext is TorrentJob job)
                {
                    if (!ReferenceEquals(_queueView.SelectedItem, job))
                    {
                        _queueView.SelectedItem = job;
                    }
                    else
                    {
                        SelectJob(job);
                    }
                }
            };
            item.GestureRecognizers.Add(tap);
            return item;
        });

    private void SetActiveTab(string name)
    {
        _activeTab = name;
        foreach ((string tabName, Button button) in _tabButtons)
        {
            bool selected = string.Equals(tabName, name, StringComparison.Ordinal);
            button.BackgroundColor = selected ? MutedBackground : Colors.Transparent;
            button.TextColor = selected ? PrimaryColor : MutedTextColor;
        }

        RefreshDetails();
    }

    private void RefreshDetails()
    {
        _detailTitle.Text = _showSettings ? "Settings" : _selectedJob?.Name ?? "Torrents";
        _detailSubtitle.Text = _showSettings ? "" : _selectedJob is null ? "" : $"{_selectedJob.Status}  \u00b7  {_selectedJob.TotalText}";
        if (!ReferenceEquals(_progressJob, _selectedJob))
        {
            _progressJob = _selectedJob;
            _progressContent.Content = _selectedJob is null ? null : BuildProgressHeader(_selectedJob);
        }

        _progressContent.IsVisible = !_showSettings && _selectedJob is not null;
        _tabStrip.IsVisible = !_showSettings && _selectedJob is not null;
        _settingsButton.BackgroundColor = _showSettings ? MutedBackground : Colors.Transparent;
        _detailContent.Content = _showSettings ? BuildOptionsPanel() : _selectedJob is null ? EmptyState("No torrent selected") : _activeTab switch
        {
            "Overview" => BuildOverviewPanel(_selectedJob),
            "Files" => BuildFilesPanel(_selectedJob),
            "Trackers" => BuildTrackersPanel(_selectedJob),
            "Peers" => BuildPeersPanel(_selectedJob),
            "Activity" => BuildLogPanel(_selectedJob),
            _ => BuildOverviewPanel(_selectedJob),
        };
    }

    private void SetQueueFilter(string filter)
    {
        _queueFilter = filter;
        foreach ((string name, Button button) in _filterButtons)
        {
            bool selected = string.Equals(name, filter, StringComparison.Ordinal);
            button.BackgroundColor = selected ? PanelBackground : Colors.Transparent;
            button.TextColor = selected ? PrimaryColor : MutedTextColor;
            button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        }

        FilterJobs();
    }

    private void FilterJobs()
    {
        string search = _searchEntry.Text?.Trim() ?? string.Empty;
        List<TorrentJob> matching = [];
        foreach (TorrentJob job in _jobs)
        {
            bool inFilter = _queueFilter switch
            {
                "Active" => job.IsRunning,
                "Done" => job.IsComplete,
                _ => true,
            };
            if (inFilter && (search.Length == 0 || job.Name.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                matching.Add(job);
            }
        }

        TorrentJob? previousSelection = _selectedJob;
        _filteringJobs = true;
        try
        {
            _visibleJobs.Clear();
            foreach (TorrentJob job in matching)
            {
                _visibleJobs.Add(job);
            }

            _selectedJob = previousSelection is not null && matching.Contains(previousSelection)
                ? previousSelection
                : matching.Count > 0 ? matching[0] : null;
            _queueView.SelectedItem = _selectedJob;
        }
        finally
        {
            _filteringJobs = false;
        }

        _queueView.EmptyView = EmptyState(_jobs.Count == 0 ? "Queue is empty" : "No matching torrents");
        _queueCountLabel.Text = search.Length == 0 && _queueFilter == "All"
            ? $"{_jobs.Count} {(_jobs.Count == 1 ? "torrent" : "torrents")}"
            : $"{matching.Count} of {_jobs.Count}";
        if (_selectedJob is null && !_showSettings)
        {
            _showQueueOnCompact = true;
        }

        RefreshSummary();
        RefreshActionState();
        if (!_showSettings)
        {
            RefreshDetails();
        }

        UpdateAdaptiveLayout();
    }

    private void RefreshSummary()
    {
        int active = 0;
        int completed = 0;
        int peers = 0;
        double downloadRate = 0;
        foreach (TorrentJob job in _jobs)
        {
            if (job.IsRunning) active++;
            if (job.IsComplete) completed++;
            if (job.IsRunning)
            {
                peers += job.ActivePeers;
                downloadRate += job.DownloadRateBytesPerSecond;
            }
        }

        _summaryLabel.Text = $"{active} active  \u00b7  {completed} complete  \u00b7  {_jobs.Count} total";
        _downloadRateLabel.Text = $"Down {TorrentJob.FormatBytes((long)downloadRate)}/s  \u00b7  {peers} {(peers == 1 ? "peer" : "peers")}";
    }

    private void StartActivityTimer()
    {
        if (_activityTimer is null)
        {
            _activityTimer = Dispatcher.CreateTimer();
            _activityTimer.Interval = TimeSpan.FromMilliseconds(120);
            _activityTimer.Tick += (_, _) =>
            {
                foreach (TorrentJob job in _jobs)
                {
                    job.DrainLogs(24);
                }

                RefreshSummary();
                RefreshActionState();
                if (!_showSettings && _selectedJob is not null)
                {
                    _detailSubtitle.Text = $"{_selectedJob.Status}  \u00b7  {_selectedJob.TotalText}";
                }
            };
        }

        _activityTimer.Start();
    }

    private void RefreshActionState()
    {
        _startButton.IsEnabled = _selectedJob?.CanStart == true;
        _pauseButton.IsEnabled = _selectedJob?.CanStop == true;
        _folderButton.IsEnabled = _selectedJob is not null;
        _copyMagnetButton.IsEnabled = _selectedJob is not null && _selectedJob.InfoHashHex.Length == 40;
        _removeButton.IsEnabled = _selectedJob is not null;
        _startButton.Opacity = _startButton.IsEnabled ? 1 : 0.35;
        _pauseButton.Opacity = _pauseButton.IsEnabled ? 1 : 0.35;
        _folderButton.Opacity = _folderButton.IsEnabled ? 1 : 0.35;
        _copyMagnetButton.Opacity = _copyMagnetButton.IsEnabled ? 1 : 0.35;
        _removeButton.Opacity = _removeButton.IsEnabled ? 1 : 0.35;
    }

    private void UpdateAdaptiveLayout()
    {
        if (Width <= 0) return;

        _isCompact = Width < 850;
        _summaryLabel.IsVisible = !_isCompact;
        bool showQueue = !_isCompact || _showQueueOnCompact && !_showSettings;
        _bodyGrid.ColumnDefinitions[0].Width = _isCompact
            ? new GridLength(showQueue ? 1 : 0, GridUnitType.Star)
            : new GridLength(336);
        _bodyGrid.ColumnDefinitions[1].Width = _isCompact
            ? new GridLength(showQueue ? 0 : 1, GridUnitType.Star)
            : GridLength.Star;
        _queuePane.IsVisible = showQueue;
        _detailsPane.IsVisible = !_isCompact || !showQueue;
        _backButton.IsVisible = _isCompact && !showQueue;
    }

    private Task ShowSettingsAsync()
    {
        if (_showSettings)
        {
            _showSettings = false;
            _showQueueOnCompact = _queueBeforeSettings;
        }
        else
        {
            _queueBeforeSettings = _showQueueOnCompact;
            _showSettings = true;
            _showQueueOnCompact = false;
        }

        RefreshDetails();
        UpdateAdaptiveLayout();
        return Task.CompletedTask;
    }

    private Task BackToQueueAsync()
    {
        _showSettings = false;
        _showQueueOnCompact = true;
        RefreshDetails();
        UpdateAdaptiveLayout();
        return Task.CompletedTask;
    }

    private View BuildOverviewPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("Add a torrent to begin.");
        }

        VerticalStackLayout stack = new() { Spacing = 16, BindingContext = job };

        Grid metrics = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 28,
        };
        metrics.Add(InfoPanel("Transfer", [
            BoundInfo("Status", nameof(TorrentJob.Status)),
            BoundInfo("Progress", nameof(TorrentJob.ProgressText)),
            BoundInfo("Downloaded", nameof(TorrentJob.DownloadedText)),
            BoundInfo("Remaining", nameof(TorrentJob.RemainingText)),
            BoundInfo("Down rate", nameof(TorrentJob.DownloadRateText)),
        ]), 0, 0);

        metrics.Add(InfoPanel("Swarm", [
            BoundInfo("Pieces", nameof(TorrentJob.PiecesText)),
            BoundInfo("Active peers", nameof(TorrentJob.ActivePeers)),
            BoundInfo("Known peers", nameof(TorrentJob.KnownPeers)),
            BoundInfo("Phase", nameof(TorrentJob.Phase)),
            BoundInfo("Message", nameof(TorrentJob.Message)),
        ]), 1, 0);
        stack.Add(metrics);

        return new ScrollView { Content = stack };
    }

    private View BuildFilesPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid();
        CollectionView files = new()
        {
            ItemsSource = job.Files,
            EmptyView = EmptyState("No files in this torrent"),
            ItemTemplate = new DataTemplate(() =>
            {
                Grid row = new()
                {
                    Padding = new Thickness(8, 6),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(new GridLength(110)),
                    },
                    ColumnSpacing = 10,
                };
                Label path = SmallLabel(string.Empty);
                path.TextColor = TextColor;
                path.SetBinding(Label.TextProperty, nameof(TorrentFileItem.Path));
                Label length = SmallLabel(string.Empty);
                length.SetBinding(Label.TextProperty, nameof(TorrentFileItem.LengthText));
                length.HorizontalTextAlignment = TextAlignment.End;
                row.Add(path, 0, 0);
                row.Add(length, 1, 0);
                return row;
            }),
        };

        grid.Add(files, 0, 0);
        return grid;
    }

    private View BuildTrackersPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid();
        CollectionView trackers = new()
        {
            ItemsSource = job.Trackers,
            EmptyView = EmptyState("No trackers in this torrent"),
            ItemTemplate = new DataTemplate(() =>
            {
                Label label = SmallLabel(string.Empty);
                label.Padding = new Thickness(8, 7);
                label.TextColor = TextColor;
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };

        grid.Add(trackers, 0, 0);
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
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 10,
        };
        grid.Add(InfoPanel("Peer Summary", [
            BoundInfo("Active peers", nameof(TorrentJob.ActivePeers)),
            BoundInfo("Known peers", nameof(TorrentJob.KnownPeers)),
            BoundInfo("DHT", nameof(TorrentJob.EffectiveDhtText)),
            BoundInfo("Trackers", nameof(TorrentJob.EffectiveTrackersText)),
        ], job), 0, 0);

        CollectionView eventsView = new()
        {
            ItemsSource = job.PeerEvents,
            EmptyView = EmptyState("No peer activity yet"),
            ItemTemplate = new DataTemplate(() =>
            {
                Label label = SmallLabel(string.Empty);
                label.Padding = new Thickness(8, 5);
                label.SetBinding(Label.TextProperty, ".");
                return label;
            }),
        };
        grid.Add(eventsView, 0, 1);
        return grid;
    }

    private View BuildLogPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        Grid grid = TabContentGrid();
        CollectionView log = new()
        {
            ItemsSource = job.LogLines,
            EmptyView = EmptyState("No activity yet"),
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
        grid.Add(log, 0, 0);
        return grid;
    }

    private static Grid TabContentGrid()
    {
        Grid grid = new()
        {
            RowDefinitions = { new RowDefinition(GridLength.Star) },
        };
        return grid;
    }

    private static View BuildProgressHeader(TorrentJob job)
    {
        Grid grid = new()
        {
            Padding = new Thickness(0, 4),
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
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            RowSpacing = 12,
            ColumnSpacing = 12,
        };

        Label name = new() { Text = "Progress", FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = TextColor };
        Label percent = SmallLabel(string.Empty);
        percent.TextColor = TextColor;
        percent.FontSize = 22;
        percent.FontAttributes = FontAttributes.Bold;
        percent.HorizontalTextAlignment = TextAlignment.End;
        percent.SetBinding(Label.TextProperty, nameof(TorrentJob.ProgressText));
        Label rate = SmallLabel(string.Empty);
        rate.HorizontalTextAlignment = TextAlignment.End;
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

        return grid;
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

    private void RestoreQueue()
    {
        IReadOnlyList<TorrentQueueEntry> entries;
        try
        {
            entries = TorrentQueueStore.Load(TorrentQueueStore.AppPath);
        }
        catch (Exception exception)
        {
            _queueStorageAvailable = false;
            _statusLabel.Text = "Queue load failed: " + exception.Message;
            return;
        }

        if (TorrentQueueStore.LastLoadError is string warning)
        {
            _statusLabel.Text = warning;
        }

        bool migrated = false;
        foreach (TorrentQueueEntry entry in entries)
        {
            TorrentJob job;
            try
            {
                string legacyPath = TorrentQueueStore.GetCachedMetainfoPath(TorrentQueueStore.AppMetainfoDirectory, entry.InfoHashHex);
                TorrentMetadata metadata;
                string sourcePath;
                try
                {
                    metadata = LoadMatchingMetadata(entry.TorrentPath, entry.InfoHashHex);
                    sourcePath = entry.TorrentPath;
                }
                catch (Exception) when (!string.Equals(legacyPath, entry.TorrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    metadata = LoadMatchingMetadata(legacyPath, entry.InfoHashHex);
                    sourcePath = legacyPath;
                }

                string durablePath = TorrentQueueStore.CacheMetainfo(sourcePath, TorrentQueueStore.AppMetainfoDirectory, entry.InfoHashHex);
                migrated |= !string.Equals(durablePath, entry.TorrentPath, StringComparison.OrdinalIgnoreCase);
                job = new TorrentJob(durablePath, entry.OutputDirectory);

                job.ApplyMetadata(metadata);
                job.BeginVerification();
                _pendingVerifications.Add((job, metadata));
            }
            catch (Exception exception)
            {
                job = new TorrentJob(entry.TorrentPath, entry.OutputDirectory)
                {
                    Name = entry.Name,
                    InfoHashHex = entry.InfoHashHex,
                };
                job.Status = "Unavailable";
                job.Message = exception.Message;
            }

            if (entry.ExplicitPeers is not null)
            {
                job.ExplicitPeers.AddRange(entry.ExplicitPeers);
            }

            TrackJob(job);
        }

        if (migrated)
        {
            SaveQueue();
        }
    }

    private static TorrentMetadata LoadMatchingMetadata(string path, string infoHashHex)
    {
        TorrentMetadata metadata = TorrentMetadata.Load(path);
        if (!string.Equals(metadata.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Torrent file changed since it was added.");
        }

        return metadata;
    }

    private void TrackJob(TorrentJob job)
    {
        job.PropertyChanged += (_, e) =>
        {
            if (ReferenceEquals(job, _selectedJob) &&
                e.PropertyName is nameof(TorrentJob.Status) or nameof(TorrentJob.Progress))
            {
                _statusLabel.Text = $"{job.Name}: {job.Status} - {job.ProgressText}";
            }

            if (e.PropertyName is nameof(TorrentJob.IsRunning) or nameof(TorrentJob.IsComplete))
            {
                FilterJobs();
            }
        };
        _jobs.Add(job);
    }

    private bool SaveQueue(TorrentJob? added = null, TorrentJob? removed = null)
    {
        if (!_queueStorageAvailable)
        {
            _statusLabel.Text = "Queue storage is unavailable; changes were not saved";
            return false;
        }

        List<TorrentQueueEntry> entries = new(_jobs.Count + (added is null ? 0 : 1));
        foreach (TorrentJob job in _jobs)
        {
            if (!ReferenceEquals(job, removed))
            {
                entries.Add(new TorrentQueueEntry(job.TorrentPath, job.OutputDirectory, job.Name, job.InfoHashHex,
                    job.ExplicitPeers.Count == 0 ? null : [.. job.ExplicitPeers]));
            }
        }

        if (added is not null)
        {
            entries.Add(new TorrentQueueEntry(added.TorrentPath, added.OutputDirectory, added.Name, added.InfoHashHex,
                added.ExplicitPeers.Count == 0 ? null : [.. added.ExplicitPeers]));
        }

        try
        {
            TorrentQueueStore.Save(TorrentQueueStore.AppPath, entries);
            return true;
        }
        catch (Exception exception)
        {
            _statusLabel.Text = "Queue save failed: " + exception.Message;
            return false;
        }
    }

    private void QueueVerification(TorrentJob job, TorrentMetadata metadata)
    {
        if (_verifications.ContainsKey(job))
        {
            return;
        }

        if (!job.IsChecking)
        {
            job.BeginVerification();
        }

        CancellationTokenSource cancellation = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _verifications.Add(job, (cancellation, completion.Task));
        _ = RunVerificationAsync(job, metadata, cancellation, completion);
    }

    private async Task RunVerificationAsync(
        TorrentJob job,
        TorrentMetadata metadata,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        try
        {
            await VerifyJobAsync(job, metadata, cancellation);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task VerifyJobAsync(TorrentJob job, TorrentMetadata metadata, CancellationTokenSource cancellation)
    {
        try
        {
            await _verificationGate.WaitAsync(cancellation.Token);
            TorrentVerificationProgress result;
            try
            {
                Progress<TorrentVerificationProgress> progress = new(snapshot =>
                {
                    if (!cancellation.IsCancellationRequested && job.IsChecking && _jobs.Contains(job))
                    {
                        job.ApplyVerificationProgress(snapshot);
                    }
                });
                result = await Task.Run(
                    () => TorrentDataVerifier.VerifyAsync(metadata, job.OutputDirectory, progress, cancellation.Token),
                    cancellation.Token);
            }
            finally
            {
                _verificationGate.Release();
            }

            if (!cancellation.IsCancellationRequested && _jobs.Contains(job))
            {
                job.CompleteVerification(result);
            }
        }
        catch (OperationCanceledException)
        {
            if (_jobs.Contains(job))
            {
                job.AbortVerification("Paused", "Data check canceled");
            }
        }
        catch (Exception exception)
        {
            if (_jobs.Contains(job))
            {
                job.AbortVerification("Error", exception.Message);
                _statusLabel.Text = $"Data check failed for {job.Name}: {exception.Message}";
            }
        }
        finally
        {
            _verifications.Remove(job);
            cancellation.Dispose();
        }
    }

    private async Task CancelVerificationAsync(TorrentJob job)
    {
        if (_verifications.TryGetValue(job, out (CancellationTokenSource Cancellation, Task Task) verification))
        {
            verification.Cancellation.Cancel();
            await verification.Task;
        }
    }

    private void CancelAllVerifications()
    {
        foreach ((CancellationTokenSource cancellation, Task _) in _verifications.Values)
        {
            cancellation.Cancel();
        }
    }

    private async Task AddTorrentAsync()
    {
        if (_importPending)
        {
            return;
        }

        _importPending = true;
        try
        {
            string? choice = await DisplayActionSheetAsync("Add torrent", "Cancel", null, "Choose a file", "Enter file path", "Enter magnet link", "Paste clipboard");
            if (choice == "Paste clipboard")
            {
                await PasteFromClipboardAsync();
                return;
            }

            string? source = choice switch
            {
                "Choose a file" => await PickTorrentFileAsync(),
                "Enter file path" => await DisplayPromptAsync("Add torrent", "Path to a .torrent file", "Add", "Cancel", "C:\\path\\file.torrent"),
                "Enter magnet link" => await DisplayPromptAsync("Add magnet link", "Paste a magnet URI", "Add", "Cancel", "magnet:?xt=urn:btih:..."),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            await AddSourceAsync(source);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Magnet import canceled";
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Add torrent failed", exception.Message, "OK");
        }
        finally
        {
            _importPending = false;
        }
    }

    private async Task PasteLinkAsync()
    {
        if (_magnetImportCancellation is not null)
        {
            _magnetImportCancellation.Cancel();
            return;
        }

        if (_importPending)
        {
            return;
        }

        _importPending = true;
        try
        {
            await PasteFromClipboardAsync();
        }
        finally
        {
            _importPending = false;
        }
    }

    private async Task PasteFromClipboardAsync()
    {
        string? source = await Clipboard.Default.GetTextAsync();
        if (string.IsNullOrWhiteSpace(source))
        {
            await DisplayAlertAsync("Clipboard is empty", "Copy a magnet link or an absolute .torrent path first.", "OK");
            return;
        }

        await AddSourceAsync(source);
    }

    private async Task AddSourceAsync(string source)
    {
        source = source.Trim().Trim('"');
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            await AddMagnetAsync(source);
            return;
        }

        if (!Path.IsPathFullyQualified(source) || !File.Exists(source))
        {
            await DisplayAlertAsync("Torrent file not found", "Enter an existing absolute .torrent path or a magnet link.", "OK");
            return;
        }

        TorrentMetadata metadata = TorrentMetadata.Load(source);
        if (SelectExistingTorrent(metadata.InfoHashHex))
        {
            return;
        }

        string cachedPath = TorrentQueueStore.CacheMetainfo(source, TorrentQueueStore.AppMetainfoDirectory, metadata.InfoHashHex);
        await AddCachedTorrentAsync(cachedPath, metadata);
    }

    private async Task AddMagnetAsync(string magnetUri)
    {
        MagnetLink link = MagnetLink.Parse(magnetUri);
        if (SelectExistingTorrent(link.InfoHashHex))
        {
            return;
        }

        using CancellationTokenSource cancellation = new();
        _magnetImportCancellation = cancellation;
        SetImportState(true);
        _statusLabel.Text = $"Finding metadata for {link.DisplayName ?? link.InfoHashHex}";
        try
        {
            MagnetResolveOptions options = _settings.ToMagnetResolveOptions();
            _magnetResolutionTask = Task.Run(() => MagnetMetadataResolver.ResolveAsync(
                magnetUri,
                options,
                message => MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (ReferenceEquals(_magnetImportCancellation, cancellation))
                    {
                        _statusLabel.Text = message;
                    }
                }),
                cancellation.Token), cancellation.Token);
            byte[] torrentBytes = await _magnetResolutionTask;
            cancellation.Token.ThrowIfCancellationRequested();
            string cachedPath = TorrentQueueStore.CacheMetainfo(torrentBytes, TorrentQueueStore.AppMetainfoDirectory, link.InfoHashHex);
            TorrentMetadata metadata = TorrentMetadata.Load(cachedPath);
            await AddCachedTorrentAsync(cachedPath, metadata, link.ExplicitPeers);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _statusLabel.Text = "Magnet import canceled";
        }
        finally
        {
            _magnetResolutionTask = null;
            _magnetImportCancellation = null;
            SetImportState(false);
        }
    }

    private void SetImportState(bool importing)
    {
        _addButton.IsEnabled = !importing;
        _importIndicator.IsVisible = importing;
        _importIndicator.IsRunning = importing;
        _pasteButton.Text = importing ? "\uE711" : "\uE8C8";
        string label = importing ? "Cancel magnet import" : "Paste link";
        ToolTipProperties.SetText(_pasteButton, label);
        SemanticProperties.SetDescription(_pasteButton, label);
    }

    private bool SelectExistingTorrent(string infoHashHex)
    {
        foreach (TorrentJob existing in _jobs)
        {
            if (string.Equals(existing.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.OutputDirectory, _settings.DefaultDownloadDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _searchEntry.Text = string.Empty;
                SetQueueFilter("All");
                _queueView.SelectedItem = existing;
                SelectJob(existing);
                _statusLabel.Text = $"{existing.Name} is already in the queue";
                return true;
            }
        }

        return false;
    }

    private async Task AddCachedTorrentAsync(string cachedPath, TorrentMetadata metadata, IReadOnlyList<string>? explicitPeers = null)
    {
        if (SelectExistingTorrent(metadata.InfoHashHex))
        {
            return;
        }

        TorrentJob job = new(cachedPath, _settings.DefaultDownloadDirectory);
        job.ApplyMetadata(metadata);
        if (explicitPeers is not null)
        {
            job.ExplicitPeers.AddRange(explicitPeers);
        }
        job.Status = "Ready";
        job.Message = $"{metadata.PieceCount} pieces, {metadata.Trackers.Count} trackers";
        if (!SaveQueue(added: job))
        {
            return;
        }

        TrackJob(job);
        _searchEntry.Text = string.Empty;
        SetQueueFilter("All");
        _queueView.SelectedItem = job;
        SelectJob(job);
        _statusLabel.Text = $"Added {job.Name}";

        if (_settings.StartOnAdd && !_settings.AddPaused)
        {
            await StartJobAsync(job);
        }
        else
        {
            QueueVerification(job, metadata);
        }
    }

    private static async Task<string?> PickTorrentFileAsync()
    {
        FilePickerFileType torrentType = new(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = [".torrent"],
        });
        FileResult? result = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Open torrent file",
            FileTypes = torrentType,
        });
        return result?.FullPath;
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
        if (!job.CanStart)
        {
            return;
        }

        try
        {
            if (!_settings.EnableDht && !_settings.EnableTrackers && job.ExplicitPeers.Count == 0)
            {
                await DisplayAlertAsync("Discovery disabled", "Enable trackers or DHT, or add a magnet with an explicit peer.", "OK");
                return;
            }

            TorrentClientOptions options = _settings.ToClientOptions(job.TorrentPath, job.OutputDirectory,
                job.HasDataToResume, job.ExplicitPeers);
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
            TorrentSession session = new(options, job.QueueLog, progress);
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
        TorrentJob? job = _selectedJob;
        if (job is null)
        {
            return;
        }

        await job.StopAsync();
        _statusLabel.Text = $"Paused {job.Name}";
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

        await CancelVerificationAsync(job);
        await job.StopAsync();
        if (!SaveQueue(removed: job))
        {
            return;
        }

        _jobs.Remove(job);
        _selectedJob = null;
        FilterJobs();
        RefreshDetails();
        RefreshActionState();
        _statusLabel.Text = $"Removed {job.Name}";
    }

    private async Task OpenSelectedFolderAsync()
    {
        string? path = _selectedJob?.PayloadPath;
        if (path is null)
        {
            return;
        }

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            await DisplayAlertAsync("Location unavailable", "This torrent has no files on disk yet.", "OK");
            return;
        }

        System.Diagnostics.ProcessStartInfo startInfo = new("explorer.exe")
        {
            UseShellExecute = false,
        };
        startInfo.Arguments = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
        System.Diagnostics.Process.Start(startInfo);
    }

    private async Task CopyMagnetAsync()
    {
        TorrentJob? job = _selectedJob;
        if (job is null || job.InfoHashHex.Length != 40)
        {
            return;
        }

        await Clipboard.Default.SetTextAsync(job.MagnetUri);
        _statusLabel.Text = $"Copied magnet link for {job.Name}";
    }

    internal async Task StopAllAsync(TimeSpan timeout)
    {
        _magnetImportCancellation?.Cancel();
        CancelAllVerifications();
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
        catch (OperationCanceledException)
        {
        }
    }

    internal void StopAllForShutdown(TimeSpan timeout)
    {
        _magnetImportCancellation?.Cancel();
        CancelAllVerifications();
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
            when (exception.Flatten().InnerExceptions.All(error => error is OperationCanceledException))
        {
        }
        catch (AggregateException exception)
        {
            _statusLabel.Text = "Shutdown stop failed: " + exception.GetBaseException().Message;
        }
    }

    private List<Task> CreateStopTasks()
    {
        List<Task> stops = [];
        if (_magnetResolutionTask is not null)
        {
            stops.Add(_magnetResolutionTask);
        }

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

    private void SetDefaultDownloadDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _settings.DefaultDownloadDirectory = value;
    }

    private Button ToolButton(string text, string icon, Func<Task> action, bool primary = false)
    {
        Button button = new()
        {
            Text = primary ? text : icon,
            FontFamily = primary ? "Segoe UI" : "Segoe Fluent Icons",
            FontSize = primary ? 13 : 18,
            CornerRadius = 4,
            HeightRequest = 38,
            MinimumHeightRequest = 38,
            WidthRequest = primary ? 128 : 40,
            MinimumWidthRequest = primary ? 128 : 40,
            Padding = primary ? new Thickness(12, 5) : new Thickness(8, 5),
            BackgroundColor = primary ? PrimaryColor : Colors.Transparent,
            TextColor = primary ? Colors.White : TextColor,
        };
        ToolTipProperties.SetText(button, text);
        SemanticProperties.SetDescription(button, text);
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

    private static View EmptyState(string text)
        => new Grid
        {
            Children =
            {
                new Label
                {
                    Text = text,
                    TextColor = MutedTextColor,
                    FontSize = 14,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                },
            },
        };

    private static Label SmallLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            TextColor = MutedTextColor,
            VerticalTextAlignment = TextAlignment.Center,
        };

    private static View InfoPanel(string title, IReadOnlyList<View> rows, object? bindingContext = null)
    {
        VerticalStackLayout stack = new()
        {
            Spacing = 12,
            Padding = new Thickness(0, 0, 0, 8),
        };
        if (bindingContext is not null)
        {
            stack.BindingContext = bindingContext;
        }

        stack.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, TextColor = TextColor, FontSize = 14, Margin = new Thickness(0, 0, 0, 3) });
        for (int i = 0; i < rows.Count; i++)
        {
            stack.Add(rows[i]);
        }

        return stack;
    }

    private static View BoundInfo(string label, string bindingPath)
    {
        Grid grid = InfoRowBase(label);
        Label value = SmallLabel(string.Empty);
        value.TextColor = TextColor;
        value.HorizontalTextAlignment = TextAlignment.End;
        value.LineBreakMode = LineBreakMode.WordWrap;
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
            Padding = new Thickness(0, 0, 0, 18),
            Spacing = 12,
        };
        stack.Add(new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = TextColor, Margin = new Thickness(0, 0, 0, 2) });
        for (int i = 0; i < rows.Count; i++)
        {
            stack.Add(rows[i]);
        }

        stack.Add(new BoxView { HeightRequest = 1, BackgroundColor = BorderColor, Margin = new Thickness(0, 6, 0, 0) });
        return stack;
    }

    private View SwitchSetting(string label, bool initial, Action<bool> update)
    {
        Grid row = SettingRowBase(label);
        Microsoft.Maui.Controls.Switch control = new()
        {
            IsToggled = initial,
            HorizontalOptions = LayoutOptions.End,
            OnColor = PrimaryColor,
            ThumbColor = Colors.White,
        };
        SemanticProperties.SetDescription(control, label);
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
        VerticalStackLayout row = new() { Spacing = 5 };
        row.Add(new Label { Text = label, FontSize = 13, TextColor = TextColor });
        Entry entry = new()
        {
            Text = initial,
            HeightRequest = 36,
            FontSize = 13,
            TextColor = TextColor,
            PlaceholderColor = MutedTextColor,
        };
        SemanticProperties.SetDescription(entry, label);
        Label validation = SmallLabel(string.Empty);
        validation.TextColor = ErrorColor;
        validation.IsVisible = false;
        void Apply()
        {
            string value = entry.Text?.Trim() ?? string.Empty;
            if (!Path.IsPathFullyQualified(value))
            {
                validation.Text = "Enter an absolute folder path";
                validation.IsVisible = true;
                return;
            }

            validation.IsVisible = false;
            update(value);
            SaveSettings();
        }

        entry.Completed += (_, _) => Apply();
        entry.Unfocused += (_, _) => Apply();
        row.Add(entry);
        row.Add(validation);
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
            TextColor = TextColor,
        };
        SemanticProperties.SetDescription(entry, label);
        row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Label validation = SmallLabel(string.Empty);
        validation.TextColor = ErrorColor;
        validation.IsVisible = false;
        void Apply()
        {
            if (int.TryParse(entry.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value) &&
                value >= minimum && value <= maximum)
            {
                validation.IsVisible = false;
                entry.TextColor = TextColor;
                update(value);
                SaveSettings();
            }
            else
            {
                validation.Text = $"Enter a number from {minimum} to {maximum}";
                validation.IsVisible = true;
                entry.TextColor = ErrorColor;
            }
        }

        entry.Completed += (_, _) => Apply();
        entry.Unfocused += (_, _) => Apply();
        row.Add(entry, 1, 0);
        row.Add(validation, 1, 1);
        return row;
    }

    private static Grid SettingRowBase(string label)
        => new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
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
