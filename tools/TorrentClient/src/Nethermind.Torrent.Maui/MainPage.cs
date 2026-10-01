// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.ObjectModel;
using Microsoft.Maui.Dispatching;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace Nethermind.Torrent.Maui;

public sealed class MainPage : ContentPage
{
    private static readonly Color PageBackground = Color.FromArgb("#F3F6F5");
    private static readonly Color PanelBackground = Color.FromArgb("#FFFFFF");
    private static readonly Color MutedBackground = Color.FromArgb("#EAF0ED");
    private static readonly Color BorderColor = Color.FromArgb("#DCE5E0");
    private static readonly Color PrimaryColor = Color.FromArgb("#087F6D");
    private static readonly Color PrimaryHover = Color.FromArgb("#0A927C");
    private static readonly Color TextColor = Color.FromArgb("#192824");
    private static readonly Color MutedTextColor = Color.FromArgb("#5E706A");
    private static readonly Color ErrorColor = Color.FromArgb("#B43D45");
    private static readonly Color RailBackground = Color.FromArgb("#202A29");
    private static readonly Color RailSurface = Color.FromArgb("#2D3937");
    private static readonly Color RailSelected = Color.FromArgb("#3B4C47");
    private static readonly Color RailSelectedHover = Color.FromArgb("#4A6057");
    private static readonly Color RailMutedText = Color.FromArgb("#A9BBB4");
    private static readonly Color StageBackground = Color.FromArgb("#173C34");
    private static readonly Color StageAccent = Color.FromArgb("#72E5AA");
    private static readonly Color StageAccentHover = Color.FromArgb("#94F2BE");
    private static readonly Color StageMutedText = Color.FromArgb("#B3D3C6");
    private static readonly Color WarmAccent = Color.FromArgb("#F3B777");
    private static readonly string IconFontFamily = OperatingSystem.IsLinux()
        ? "DejaVu Sans"
        : OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? "Segoe Fluent Icons"
            : "Segoe MDL2 Assets";

    private static string PlatformIcon(string icon)
    {
        if (!OperatingSystem.IsLinux())
        {
            return icon;
        }

        return icon switch
        {
            "\uE768" => "▶",
            "\uE769" => "Ⅱ",
            "\uE8B7" => "▤",
            "\uE71B" => "↗",
            "\uE74D" or "\uE711" => "×",
            "\uE8C8" => "▣",
            "\uE712" => "⋯",
            "\uE713" => "⚙",
            "\uE721" => "⌕",
            "\uE72B" => "←",
            _ => icon,
        };
    }

    private readonly ObservableCollection<TorrentJob> _jobs = [];
    private readonly ObservableCollection<TorrentJob> _visibleJobs = [];
    private readonly List<(TorrentJob Job, TorrentMetadata Metadata)> _pendingVerifications = [];
    private readonly Dictionary<TorrentJob, (CancellationTokenSource Cancellation, Task Task)> _verifications = [];
    private readonly SemaphoreSlim _verificationGate = new(1, 1);
    private readonly TorrentUiSettings _settings = TorrentUiSettingsStore.Load();
    internal bool MinimizeToTrayEnabled => _settings.MinimizeToTray;
    private readonly CollectionView _queueView;
    private readonly ContentView _queueEmptyContent = new() { IsVisible = false };
    private readonly ContentView _detailContent = new();
    private readonly Label _statusLabel = SmallLabel("Ready");
    private readonly Grid _statusBar = [];
    private readonly Label _queueCountLabel = SmallLabel("0 torrents");
    private readonly Label _summaryLabel = SmallLabel("0 active");
    private readonly Label _detailTitle = new() { FontSize = 24, FontAttributes = FontAttributes.Bold, TextColor = TextColor, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _detailSubtitle = SmallLabel(string.Empty);
    private readonly Dictionary<string, Button> _tabButtons = [];
    private readonly Dictionary<string, BoxView> _tabIndicators = [];
    private readonly Entry _searchEntry = new()
    {
        Placeholder = "Search library",
        FontSize = 13,
        HeightRequest = 36,
        TextColor = Colors.White,
        PlaceholderColor = RailMutedText,
        BackgroundColor = Colors.Transparent,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
    };
    private readonly Dictionary<string, Button> _filterButtons = [];
    private readonly Dictionary<Button, Action> _buttonFeedback = [];
    private readonly Button _startButton;
    private readonly Button _pauseButton;
    private readonly Button _removeButton;
    private readonly Button _folderButton;
    private readonly Button _copyMagnetButton;
    private readonly Button _addButton;
    private readonly Button _pasteButton;
    private readonly Button _moreButton;
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
    private DateTimeOffset _nextTransferRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _nextTransferSave = DateTimeOffset.MinValue;
    private DateTimeOffset _nextFeedRefresh = DateTimeOffset.UtcNow.AddMinutes(1);
    private readonly CancellationTokenSource _feedRefreshCancellation = new();
    private Task? _feedRefreshTask;
    private CancellationTokenSource? _magnetImportCancellation;
    private Task<byte[]>? _magnetResolutionTask;
    private bool _importPending;

    internal static MainPage? Active { get; private set; }

    public MainPage()
    {
        Active = this;
        Title = "Nethermind Torrent Client";
        BackgroundColor = PageBackground;
        Shell.SetNavBarIsVisible(this, false);
        _queueView = CreateQueueView();
        _startButton = ToolButton("Start", "\uE768", StartSelectedAsync);
        _pauseButton = ToolButton("Pause", "\uE769", PauseSelectedAsync);
        _folderButton = ToolButton("Open folder", "\uE8B7", OpenSelectedFolderAsync);
        _copyMagnetButton = ToolButton("Copy magnet link", "\uE71B", CopyMagnetAsync);
        _removeButton = ToolButton("Remove", "\uE74D", RemoveSelectedAsync);
        _addButton = ToolButton("Add torrent", "+", AddTorrentAsync, primary: true);
        _pasteButton = ToolButton("Paste link", "\uE8C8", PasteLinkAsync);
        _moreButton = ToolButton("More actions", "\uE712", ShowMoreActionsAsync);
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
            _feedRefreshCancellation.Cancel();
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
            Padding = new Thickness(16, 10),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
            BackgroundColor = RailBackground,
        };

        Image mark = new() { Source = OperatingSystem.IsLinux() ? "ntmark.svg" : "ntmark.png", WidthRequest = 28, HeightRequest = 28, Aspect = Aspect.AspectFit, VerticalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(mark, "Nethermind Torrent Client");
        toolbar.Add(mark, 0, 0);

        HorizontalStackLayout commands = new() { Spacing = 4 };
        commands.Add(_addButton);
        commands.Add(_pasteButton);
        commands.Add(_startButton);
        commands.Add(_pauseButton);
        commands.Add(_folderButton);
        commands.Add(_copyMagnetButton);
        commands.Add(_removeButton);
        commands.Add(_moreButton);
        toolbar.Add(commands, 1, 0);
        toolbar.Add(_importIndicator, 2, 0);
        _summaryLabel.TextColor = RailMutedText;
        toolbar.Add(_summaryLabel, 3, 0);
        _settingsButton = ToolButton("Settings", "\uE713", ShowSettingsAsync, selected: () => _showSettings);
        toolbar.Add(_settingsButton, 4, 0);

        return toolbar;
    }

    private View BuildBody()
    {
        _bodyGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(320)),
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
            Padding = new Thickness(16, 20, 16, 0),
            RowSpacing = 14,
        };

        Grid heading = new()
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
        };
        heading.Add(new Label { Text = "Library", FontSize = 20, FontAttributes = FontAttributes.Bold, TextColor = Colors.White }, 0, 0);
        _queueCountLabel.TextColor = RailMutedText;
        heading.Add(_queueCountLabel, 1, 0);
        queue.Add(heading, 0, 0);

        _searchEntry.TextChanged += (_, _) => FilterJobs();
        RemoveNativeSearchBorder(_searchEntry);
        Grid search = new()
        {
            Padding = new Thickness(10, 0),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 7,
        };
        search.Add(new Label { Text = PlatformIcon("\uE721"), FontFamily = IconFontFamily, FontSize = 16, TextColor = RailMutedText, VerticalTextAlignment = TextAlignment.Center }, 0, 0);
        search.Add(_searchEntry, 1, 0);
        Border searchFrame = new()
        {
            StrokeThickness = 1,
            Stroke = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = RailSurface,
            Content = search,
        };
        _searchEntry.Focused += (_, _) => searchFrame.Stroke = StageAccent;
        _searchEntry.Unfocused += (_, _) => searchFrame.Stroke = Colors.Transparent;
        queue.Add(searchFrame, 0, 1);

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
                BackgroundColor = Colors.Transparent,
                TextColor = RailMutedText,
            };
            button.Clicked += (_, _) => SetQueueFilter(filter);
            AddHoverFeedback(button, hovered =>
            {
                bool selected = string.Equals(_queueFilter, filter, StringComparison.Ordinal);
                if (selected)
                {
                    return (hovered ? StageAccentHover : StageAccent, RailBackground);
                }

                return (hovered ? RailSelected : Colors.Transparent, hovered ? Colors.White : RailMutedText);
            });
            _filterButtons[filter] = button;
            filters.Add(button);
        }
        queue.Add(filters, 0, 2);
        Grid queueContent = new() { Children = { _queueView, _queueEmptyContent } };
        queue.Add(queueContent, 0, 3);

        Grid frame = new()
        {
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(1)) },
            BackgroundColor = RailBackground,
        };
        frame.Add(queue, 0, 0);
        frame.Add(new BoxView { BackgroundColor = RailSurface }, 1, 0);
        return frame;
    }

    private View BuildDetailsPanel()
    {
        Grid details = new()
        {
            Padding = 0,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 0,
        };

        Grid heading = new()
        {
            Padding = new Thickness(26, 20, 26, 18),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 10,
        };
        _backButton = ToolButton("Back to queue", "\uE72B", BackToQueueAsync, onLightSurface: true);
        _backButton.IsVisible = false;
        heading.Add(_backButton, 0, 0);
        VerticalStackLayout title = new() { Spacing = 2 };
        title.Add(_detailTitle);
        title.Add(_detailSubtitle);
        heading.Add(title, 1, 0);
        details.Add(heading, 0, 0);

        details.Add(_progressContent, 0, 1);
        _tabStrip = BuildTabStrip();
        _tabStrip.Padding = new Thickness(26, 14, 26, 0);
        details.Add(_tabStrip, 0, 2);
        _detailContent.Margin = new Thickness(26, 18, 26, 16);
        details.Add(_detailContent, 0, 3);
        return details;
    }

    private HorizontalStackLayout BuildTabStrip()
    {
        HorizontalStackLayout tabs = new() { Spacing = 16, HorizontalOptions = LayoutOptions.Start };

        string[] names = ["Overview", "Files", "Trackers", "Peers", "Activity"];
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i];
            Button button = new()
            {
                Text = name,
                CornerRadius = 0,
                HeightRequest = 36,
                MinimumHeightRequest = 36,
                Padding = new Thickness(0, 3),
                FontSize = 13,
                BackgroundColor = Colors.Transparent,
                TextColor = MutedTextColor,
            };
            button.Clicked += (_, _) => SetActiveTab(name);
            AddHoverFeedback(button, hovered =>
            {
                bool selected = string.Equals(_activeTab, name, StringComparison.Ordinal);
                return (hovered ? MutedBackground : Colors.Transparent,
                    selected ? TextColor : hovered ? PrimaryColor : MutedTextColor);
            });
            _tabButtons[name] = button;
            BoxView indicator = new() { HeightRequest = 3, BackgroundColor = WarmAccent, IsVisible = false };
            _tabIndicators[name] = indicator;
            VerticalStackLayout tab = new()
            {
                Spacing = 0,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = name switch
                {
                    "Overview" => 84,
                    "Trackers" => 72,
                    "Activity" => 70,
                    _ => 48,
                },
            };
            tab.Add(button);
            tab.Add(indicator);
            tabs.Add(tab);
        }

        return tabs;
    }

    private View BuildStatusBar()
    {
        _statusLabel.LineBreakMode = LineBreakMode.TailTruncation;
        _statusBar.Padding = new Thickness(18, 7);
        _statusBar.BackgroundColor = MutedBackground;
        _statusBar.IsVisible = false;
        _statusBar.Add(_statusLabel, 0, 0);
        _statusLabel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == Label.TextProperty.PropertyName)
            {
                _statusBar.IsVisible = _statusLabel.Text != "Ready";
            }
        };
        return _statusBar;
    }

    private CollectionView CreateQueueView()
    {
        CollectionView view = new()
        {
            ItemsSource = _visibleJobs,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = CreateQueueTemplate(),
            BackgroundColor = RailBackground,
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
                Padding = new Thickness(12, 14),
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
                RowSpacing = 6,
                ColumnSpacing = 8,
            };

            Label name = new() { FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
            name.SetBinding(Label.TextProperty, nameof(TorrentJob.Name));
            Label status = SmallLabel(string.Empty);
            status.TextColor = StageAccent;
            status.SetBinding(Label.TextProperty, nameof(TorrentJob.Status));
            ProgressBar progress = BoundProgressBar(6, onDark: true);
            Label progressText = SmallLabel(string.Empty);
            progressText.TextColor = Colors.White;
            progressText.SetBinding(Label.TextProperty, nameof(TorrentJob.ProgressText));
            Label message = SmallLabel(string.Empty);
            message.TextColor = RailMutedText;
            message.LineBreakMode = LineBreakMode.TailTruncation;
            message.SetBinding(Label.TextProperty, nameof(TorrentJob.QueueDetailText));

            grid.Add(name, 0, 0);
            grid.Add(status, 1, 0);
            grid.Add(progress, 0, 1);
            Grid.SetColumnSpan(progress, 2);
            grid.Add(progressText, 0, 2);
            grid.Add(message, 0, 3);
            Grid.SetColumnSpan(message, 2);

            Border item = new()
            {
                Margin = new Thickness(0, 0, 0, 8),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                BackgroundColor = RailSurface,
                Content = grid,
            };
            VisualState selected = new() { Name = "Selected" };
            selected.Setters.Add(new Setter { Property = Border.BackgroundColorProperty, Value = RailSelected });
            VisualStateGroup group = new() { Name = "CommonStates" };
            group.States.Add(new VisualState { Name = "Normal" });
            group.States.Add(selected);
            VisualStateGroupList states = [group];
            VisualStateManager.SetVisualStateGroups(item, states);
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
            button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            _tabIndicators[tabName].IsVisible = selected;
            SemanticProperties.SetDescription(button, selected ? $"{tabName}, selected tab" : $"{tabName} tab");
            _buttonFeedback[button]();
        }

        RefreshDetails();
    }

    private void RefreshDetails()
    {
        _detailTitle.Text = _showSettings ? "Settings" : _selectedJob?.Name ?? "Torrents";
        _detailSubtitle.Text = _showSettings || _selectedJob is null ? "" : DetailSubtitle(_selectedJob);
        if (!ReferenceEquals(_progressJob, _selectedJob))
        {
            _progressJob = _selectedJob;
            _progressContent.Content = _selectedJob is null ? null : BuildProgressHeader(_selectedJob);
        }

        _progressContent.IsVisible = !_showSettings && _selectedJob is not null;
        _tabStrip.IsVisible = !_showSettings && _selectedJob is not null;
        _buttonFeedback[_settingsButton]();
        _detailContent.Content = _showSettings ? BuildOptionsPanel() : _selectedJob is null ? BuildDetailEmptyView() : _activeTab switch
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
            button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            SemanticProperties.SetDescription(button, selected ? $"{name}, selected filter" : $"{name} filter");
            _buttonFeedback[button]();
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

        if (matching.Count == 0)
        {
            _queueEmptyContent.Content = BuildQueueEmptyView(_jobs.Count == 0);
        }

        _queueEmptyContent.IsVisible = matching.Count == 0;
        _queueView.IsVisible = matching.Count > 0;
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
        int seeding = 0;
        foreach (TorrentJob job in _jobs)
        {
            if (job.IsRunning) active++;
            if (job.IsComplete) completed++;
            if (job.Status == "Seeding" && job.IsRunning) seeding++;
        }

        _summaryLabel.Text = $"{active} active  \u00b7  {seeding} seeding  \u00b7  {completed} complete  \u00b7  {_jobs.Count} total";
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

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= _nextTransferRefresh)
                {
                    bool hasRunningJob = false;
                    foreach (TorrentJob job in _jobs)
                    {
                        if (job.IsRunning)
                        {
                            job.RefreshTransfer(ReferenceEquals(job, _selectedJob) && _activeTab == "Files" &&
                                !_showSettings && _detailsPane.IsVisible && IsAppWindowVisible());
                            hasRunningJob = true;
                        }
                    }

                    _nextTransferRefresh = now + TimeSpan.FromSeconds(1);
                    if (hasRunningJob && now >= _nextTransferSave)
                    {
                        SaveQueue();
                        _nextTransferSave = now + TimeSpan.FromSeconds(10);
                    }
                }

                if (now >= _nextFeedRefresh && _feedRefreshTask is not { IsCompleted: false } &&
                    _magnetImportCancellation is null && _settings.EnableDht && !_feedRefreshCancellation.IsCancellationRequested)
                {
                    _nextFeedRefresh = now.AddMinutes(15);
                    _feedRefreshTask = RefreshFeedsAsync(_feedRefreshCancellation.Token);
                }

                RefreshSummary();
                RefreshActionState();
                if (!_showSettings && _selectedJob is not null)
                {
                    _detailSubtitle.Text = DetailSubtitle(_selectedJob);
                }
            };
        }

        _activityTimer.Start();
    }

    private async Task RefreshFeedsAsync(CancellationToken token)
    {
        foreach (TorrentJob previous in _jobs.ToArray())
        {
            if (previous.Bep46FeedUri is not string feedUri || previous.Bep46Sequence is not long seenSequence)
            {
                continue;
            }

            try
            {
                token.ThrowIfCancellationRequested();
                Bep46Link feed = Bep46Link.Parse(feedUri);
                Bep46Update? update = await feed.GetCurrentAsync(token);
                token.ThrowIfCancellationRequested();
                if (!_jobs.Contains(previous) || !string.Equals(previous.Bep46FeedUri, feedUri, StringComparison.Ordinal) ||
                    previous.Bep46Sequence != seenSequence)
                {
                    continue;
                }

                if (update is null || update.Sequence <= seenSequence)
                {
                    continue;
                }

                string infoHashHex = Convert.ToHexString(update.InfoHash).ToLowerInvariant();
                string downloadRoot = previous.Bep46DownloadRoot ?? previous.OutputDirectory;
                string versionDirectory = Bep46StorageLayout.VersionDirectory(downloadRoot, feed, infoHashHex);
                TorrentJob? existing = null;
                foreach (TorrentJob job in _jobs)
                {
                    if (string.Equals(job.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(job.OutputDirectory, versionDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = job;
                        break;
                    }
                }

                if (existing is null)
                {
                    string magnet = feed.ToMagnet(update);
                    byte[] metainfo = await MagnetMetadataResolver.ResolveAsync(magnet, _settings.ToMagnetResolveOptions(),
                        null, token);
                    token.ThrowIfCancellationRequested();
                    if (!_jobs.Contains(previous) || !string.Equals(previous.Bep46FeedUri, feedUri, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string cachedPath = TorrentQueueStore.CacheMetainfo(metainfo, TorrentQueueStore.AppMetainfoDirectory, infoHashHex);
                    TorrentMetadata metadata = TorrentMetadata.Load(cachedPath);
                    await AddCachedTorrentAsync(cachedPath, metadata, MagnetLink.Parse(magnet).ExplicitPeers,
                        feedUri, update.Sequence, versionDirectory, downloadRoot, previous);
                    foreach (TorrentJob job in _jobs)
                    {
                        if (string.Equals(job.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(job.OutputDirectory, versionDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            existing = job;
                            break;
                        }
                    }
                }

                if (existing is not null && previous.Bep46FeedUri is not null)
                {
                    string? priorFeed = existing.Bep46FeedUri;
                    long? priorSequence = existing.Bep46Sequence;
                    string? priorRoot = existing.Bep46DownloadRoot;
                    string? previousRoot = previous.Bep46DownloadRoot;
                    existing.Bep46FeedUri = feedUri;
                    existing.Bep46Sequence = update.Sequence;
                    existing.Bep46DownloadRoot = downloadRoot;
                    if (!ReferenceEquals(existing, previous))
                    {
                        previous.Bep46FeedUri = null;
                        previous.Bep46Sequence = null;
                        previous.Bep46DownloadRoot = null;
                    }

                    if (!SaveQueue())
                    {
                        existing.Bep46FeedUri = priorFeed;
                        existing.Bep46Sequence = priorSequence;
                        existing.Bep46DownloadRoot = priorRoot;
                        previous.Bep46FeedUri = feedUri;
                        previous.Bep46Sequence = seenSequence;
                        previous.Bep46DownloadRoot = previousRoot;
                        continue;
                    }
                }

                if (existing is not null)
                {
                    _statusLabel.Text = $"Feed updated to {existing.Name}";
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _statusLabel.Text = $"Feed refresh failed: {exception.Message}";
            }
        }
    }

#if WINDOWS
    private bool IsAppWindowVisible() => Window?.Handler?.PlatformView is not MauiWinUIWindow nativeWindow ||
            nativeWindow.AppWindow.IsVisible;
#else
    private bool IsAppWindowVisible() => true;
#endif

    private void RefreshActionState()
    {
        _startButton.IsEnabled = _selectedJob?.CanStart == true;
        string startAction = _selectedJob?.IsComplete == true ? "Seed" : "Start";
        ToolTipProperties.SetText(_startButton, startAction);
        SemanticProperties.SetDescription(_startButton, startAction);
        _pauseButton.IsEnabled = _selectedJob?.CanStop == true;
        _folderButton.IsEnabled = _selectedJob is not null;
        _copyMagnetButton.IsEnabled = _selectedJob is not null && _selectedJob.InfoHashHex.Length == 40;
        _removeButton.IsEnabled = _selectedJob is not null;
        _moreButton.IsEnabled = _selectedJob is not null;
        _startButton.Opacity = _startButton.IsEnabled ? 1 : 0.35;
        _pauseButton.Opacity = _pauseButton.IsEnabled ? 1 : 0.35;
        _folderButton.Opacity = _folderButton.IsEnabled ? 1 : 0.35;
        _copyMagnetButton.Opacity = _copyMagnetButton.IsEnabled ? 1 : 0.35;
        _removeButton.Opacity = _removeButton.IsEnabled ? 1 : 0.35;
        _moreButton.Opacity = _moreButton.IsEnabled ? 1 : 0.35;
    }

    private void UpdateAdaptiveLayout()
    {
        if (Width <= 0) return;

        _isCompact = Width < 850;
        _summaryLabel.IsVisible = Width >= 1000;
        bool overflow = Width < 740;
        _folderButton.IsVisible = !overflow;
        _copyMagnetButton.IsVisible = !overflow;
        _removeButton.IsVisible = !overflow;
        _moreButton.IsVisible = overflow;
        bool showQueue = !_isCompact || _showQueueOnCompact && !_showSettings;
        _bodyGrid.ColumnDefinitions[0].Width = _isCompact
            ? new GridLength(showQueue ? 1 : 0, GridUnitType.Star)
            : new GridLength(320);
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

    private async Task ShowMoreActionsAsync()
    {
        TorrentJob? job = _selectedJob;
        if (job is null)
        {
            return;
        }

        List<string> actions = ["Open folder"];
        if (_copyMagnetButton.IsEnabled)
        {
            actions.Add("Copy magnet link");
        }

        actions.Add("Remove torrent");
        string? choice = await DisplayActionSheetAsync("Torrent actions", "Cancel", null, [.. actions]);
        if (choice is null or "Cancel")
        {
            return;
        }

        if (!ReferenceEquals(_selectedJob, job))
        {
            _statusLabel.Text = "Selection changed; choose an action again";
            return;
        }

        switch (choice)
        {
            case "Open folder":
                await OpenSelectedFolderAsync();
                break;
            case "Copy magnet link":
                await CopyMagnetAsync();
                break;
            case "Remove torrent":
                await RemoveSelectedAsync();
                break;
        }
    }

    private static string DetailSubtitle(TorrentJob job)
        => $"{job.TotalText}  \u00b7  {job.Files.Count} {(job.Files.Count == 1 ? "file" : "files")}";

    private View BuildOverviewPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("Add a torrent to begin.");
        }

        VerticalStackLayout stack = new() { Spacing = 16, BindingContext = job };
        if (!job.IsComplete)
        {
            stack.Add(new Label { Text = "Status", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = TextColor });
            Label summary = SmallLabel(string.Empty);
            summary.TextColor = TextColor;
            summary.LineBreakMode = LineBreakMode.WordWrap;
            summary.SetBinding(Label.TextProperty, nameof(TorrentJob.OverviewSummaryText));
            stack.Add(summary);
            stack.Add(new BoxView { HeightRequest = 1, BackgroundColor = BorderColor });
        }

        Grid details = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 20,
        };
        details.Add(InfoPanel("Integrity", [
            BoundInfo("Verified pieces", nameof(TorrentJob.PiecesText)),
        ]), 0, 0);
        details.Add(InfoPanel("Storage", [
            BoundInfo("Folder", nameof(TorrentJob.OutputFolderText), tooltipPath: nameof(TorrentJob.OutputDirectory)),
        ]), 1, 0);
        stack.Add(details);

        if (job.TransferHistory is not null)
        {
            stack.Add(new BoxView { HeightRequest = 1, BackgroundColor = BorderColor });
            stack.Add(InfoPanel("Network transfer", [
                BoundInfo("Payload received", nameof(TorrentJob.NetworkReceivedText)),
                BoundInfo("Verified from peers", nameof(TorrentJob.VerifiedFromPeersText)),
                BoundInfo("Uploaded", nameof(TorrentJob.UploadedText)),
                BoundInfo("Verified average while active", nameof(TorrentJob.ActiveAverageText)),
                BoundInfo("Active time", nameof(TorrentJob.ActiveTimeText)),
                BoundInfo("Contributing peers", nameof(TorrentJob.ContributorText)),
            ]));
        }

        return new ScrollView { Content = stack };
    }

    private View BuildFilesPanel(TorrentJob? job)
    {
        if (job is null)
        {
            return EmptyState("No torrent selected.");
        }

        _ = job.RefreshAvailabilityAsync();

        Grid grid = new()
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            RowSpacing = 12,
        };
        Entry search = new()
        {
            Placeholder = "Search files",
            ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
            FontSize = 13,
            HeightRequest = 36,
            TextColor = TextColor,
            PlaceholderColor = MutedTextColor,
            BackgroundColor = Colors.Transparent,
        };
        RemoveNativeSearchBorder(search);
        Border searchFrame = new()
        {
            Stroke = BorderColor,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            BackgroundColor = PanelBackground,
            Padding = new Thickness(10, 0),
            Content = search,
        };
        search.Focused += (_, _) => searchFrame.Stroke = PrimaryColor;
        search.Unfocused += (_, _) => searchFrame.Stroke = BorderColor;
        Label count = SmallLabel(string.Empty);
        count.HorizontalTextAlignment = TextAlignment.End;
        Grid searchRow = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
        };
        searchRow.Add(searchFrame, 0, 0);
        searchRow.Add(count, 1, 0);
        grid.Add(searchRow, 0, 0);

        Grid fileHeader = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(88)),
                new ColumnDefinition(new GridLength(92)),
            },
            ColumnSpacing = 10,
        };
        Label fileNameHeader = SmallLabel("FILE");
        fileNameHeader.Padding = new Thickness(8, 0);
        fileHeader.Add(fileNameHeader, 0, 0);
        Label sizeHeader = SmallLabel("SIZE");
        sizeHeader.HorizontalTextAlignment = TextAlignment.End;
        fileHeader.Add(sizeHeader, 1, 0);
        Label availabilityHeader = SmallLabel("AVAILABLE");
        availabilityHeader.HorizontalTextAlignment = TextAlignment.End;
        ToolTipProperties.SetText(availabilityHeader, "Verified locally or advertised by connected download peers. A snapshot, not a guarantee.");
        fileHeader.Add(availabilityHeader, 2, 0);

        CollectionView files = new()
        {
            ItemsSource = job.Files,
            HorizontalOptions = LayoutOptions.Fill,
            ItemTemplate = new DataTemplate(() => CreateFileRow(job)),
        };
        ContentView empty = new();
        Grid list = new()
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            RowSpacing = 4,
        };
        list.Add(fileHeader, 0, 0);
        list.Add(files, 0, 1);
        list.Add(empty, 0, 1);
        grid.Add(list, 0, 1);

        void UpdateFilter()
        {
            IReadOnlyList<TorrentFileItem> matches = job.FindFiles(search.Text);
            files.ItemsSource = matches;
            count.Text = matches.Count == job.Files.Count ? $"{job.Files.Count} files" : $"{matches.Count} of {job.Files.Count}";
            empty.Content = EmptyState(job.Files.Count == 0 ? "No files in this torrent" : "No matching files");
            empty.IsVisible = matches.Count == 0;
            files.IsVisible = matches.Count > 0;
        }

        search.TextChanged += (_, _) => UpdateFilter();
        UpdateFilter();
        return grid;
    }

    private View CreateFileRow(TorrentJob job)
    {
        Grid row = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(88)),
                new ColumnDefinition(new GridLength(92)),
            },
            ColumnSpacing = 10,
        };
        Button reveal = new()
        {
            HeightRequest = 38,
            MinimumHeightRequest = 38,
            Padding = 0,
            CornerRadius = 4,
            BackgroundColor = Colors.Transparent,
        };
        reveal.SetBinding(SemanticProperties.DescriptionProperty,
            new Binding(nameof(TorrentFileItem.Path), stringFormat: "Reveal {0} in file manager"));
        ToolTipProperties.SetText(reveal, "Show in file manager");
        reveal.Clicked += (_, _) =>
        {
            if (reveal.BindingContext is TorrentFileItem file)
            {
                _ = RunUiActionAsync(() => RevealTorrentFileAsync(job, file));
            }
        };
        AddHoverFeedback(reveal, hovered => (hovered ? MutedBackground : Colors.Transparent,
            hovered ? PrimaryColor : TextColor), track: false);
        Label path = SmallLabel(string.Empty);
        path.TextColor = TextColor;
        path.Padding = new Thickness(8, 0);
        path.LineBreakMode = OperatingSystem.IsLinux() ? LineBreakMode.NoWrap : LineBreakMode.TailTruncation;
        path.MaxLines = 1;
        path.InputTransparent = true;
#if LINUX
        path.HandlerChanged += (_, _) =>
        {
            if (path.Handler?.PlatformView is Gtk.Label gtkLabel)
            {
                gtkLabel.Wrap = false;
                gtkLabel.Ellipsize = Pango.EllipsizeMode.End;
            }
        };
#endif
        path.SetBinding(Label.TextProperty, nameof(TorrentFileItem.Path));
        Label length = SmallLabel(string.Empty);
        length.SetBinding(Label.TextProperty, nameof(TorrentFileItem.LengthText));
        length.HorizontalTextAlignment = TextAlignment.End;
        length.InputTransparent = true;
        row.Add(reveal, 0, 0);
        Grid.SetColumnSpan(reveal, 3);
        row.Add(path, 0, 0);
        row.Add(length, 1, 0);
        Label availability = SmallLabel(string.Empty);
        availability.SetBinding(Label.TextProperty, nameof(TorrentFileItem.AvailabilityText));
        availability.HorizontalTextAlignment = TextAlignment.End;
        availability.InputTransparent = true;
        ToolTipProperties.SetText(availability, "Verified locally or advertised by connected download peers. -- means unknown.");
        row.Add(availability, 2, 0);
        return row;
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
        grid.Add(job.IsRunning
            ? InfoPanel("Discovery diagnostics", [
                BoundInfo("Active peer tasks", nameof(TorrentJob.ActivePeers)),
                BoundInfo("Active upload peers", nameof(TorrentJob.ActiveUploadPeers)),
                BoundInfo("Listening TCP port", nameof(TorrentJob.ListeningPortText)),
                BoundInfo("Known addresses this run", nameof(TorrentJob.KnownPeers)),
                BoundInfo("DHT enabled", nameof(TorrentJob.EffectiveDhtText)),
                BoundInfo("Trackers enabled", nameof(TorrentJob.EffectiveTrackersText)),
            ], job)
            : SmallLabel("No peer session is running."), 0, 0);

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

    private View BuildProgressHeader(TorrentJob job)
    {
        Grid grid = new()
        {
            Padding = new Thickness(26, 14, 26, 16),
            BackgroundColor = StageBackground,
            BindingContext = job,
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
            RowSpacing = 7,
            ColumnSpacing = 12,
        };

        Label status = new() { FontAttributes = FontAttributes.Bold, FontSize = 12, TextColor = StageAccent };
        status.SetBinding(Label.TextProperty, nameof(TorrentJob.StageStateText));
        Button action = new()
        {
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            CornerRadius = 6,
            HeightRequest = 32,
            MinimumHeightRequest = 32,
            Padding = new Thickness(14, 4),
        };
        action.SetBinding(Button.TextProperty, nameof(TorrentJob.StageActionText));
        action.SetBinding(VisualElement.IsVisibleProperty, nameof(TorrentJob.StageActionAvailable));
        action.Clicked += (_, _) => _ = RunUiActionAsync(() => RunStageActionAsync(job));
        AddHoverFeedback(action, hovered => (hovered ? StageAccentHover : StageAccent, RailBackground), track: false);
        Label percent = SmallLabel(string.Empty);
        percent.TextColor = Colors.White;
        percent.FontSize = 34;
        percent.FontAttributes = FontAttributes.Bold;
        percent.SetBinding(Label.TextProperty, nameof(TorrentJob.ProgressText));
        ProgressBar progress = BoundProgressBar(8, onDark: true, description: "Selected torrent progress");
        Grid summary = new()
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
        };
        Label verified = SmallLabel(string.Empty);
        verified.TextColor = StageMutedText;
        verified.LineBreakMode = LineBreakMode.TailTruncation;
        verified.SetBinding(Label.TextProperty, nameof(TorrentJob.VerifiedSummaryText));
        Label remaining = SmallLabel(string.Empty);
        remaining.TextColor = StageMutedText;
        remaining.HorizontalTextAlignment = TextAlignment.End;
        remaining.SetBinding(Label.TextProperty, nameof(TorrentJob.RemainingSummaryText));
        summary.Add(verified, 0, 0);
        summary.Add(remaining, 1, 0);

        grid.Add(status, 0, 0);
        grid.Add(action, 1, 0);
        grid.Add(percent, 0, 1);
        grid.Add(progress, 0, 2);
        Grid.SetColumnSpan(progress, 2);
        grid.Add(summary, 0, 3);
        Grid.SetColumnSpan(summary, 2);

        return grid;
    }

    private Task RunStageActionAsync(TorrentJob job)
    {
        if (!ReferenceEquals(_selectedJob, job))
        {
            return Task.CompletedTask;
        }

        return job.CanStop ? PauseSelectedAsync() : StartJobAsync(job);
    }

    private static ProgressBar BoundProgressBar(double height, bool onDark = false, string description = "Torrent progress")
    {
        ProgressBar progress = new()
        {
            HeightRequest = height,
            BackgroundColor = onDark ? RailSelected : MutedBackground,
            ProgressColor = onDark ? StageAccent : PrimaryColor,
        };
        progress.SetBinding(ProgressBar.ProgressProperty, nameof(TorrentJob.Progress));
        SemanticProperties.SetDescription(progress, description);
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
            NumberSetting("Upload peers per torrent", _settings.MaxUploadPeers, value => _settings.MaxUploadPeers = value, 1, 64),
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
            SwitchSetting("Seed after download completes", _settings.SeedAfterCompletion, value => _settings.SeedAfterCompletion = value),
            ReadOnlySetting("Active torrents", "Manual"),
            ReadOnlySetting("Ratio limit", "Not available"),
            ReadOnlySetting("Seed time minutes", "Not available"),
        ]));

        if (OperatingSystem.IsWindows())
        {
            stack.Add(SettingsSection("Window", [
                SwitchSetting("Minimize to tray", _settings.MinimizeToTray, value => _settings.MinimizeToTray = value),
            ]));
        }

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
                job.RestoreTransferHistory(entry.Transfer);
                job.ResumeSeeding = entry.ResumeSeeding;
                job.Bep46FeedUri = entry.Bep46FeedUri;
                job.Bep46Sequence = entry.Bep46Sequence;
                job.Bep46DownloadRoot = entry.Bep46DownloadRoot;
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
                job.RestoreTransferHistory(entry.Transfer);
                job.ResumeSeeding = entry.ResumeSeeding;
                job.Bep46FeedUri = entry.Bep46FeedUri;
                job.Bep46Sequence = entry.Bep46Sequence;
                job.Bep46DownloadRoot = entry.Bep46DownloadRoot;
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
                    job.ExplicitPeers.Count == 0 ? null : [.. job.ExplicitPeers], job.TransferHistory, job.ResumeSeeding,
                    job.Bep46FeedUri, job.Bep46Sequence, job.Bep46DownloadRoot));
            }
        }

        if (added is not null)
        {
            entries.Add(new TorrentQueueEntry(added.TorrentPath, added.OutputDirectory, added.Name, added.InfoHashHex,
                added.ExplicitPeers.Count == 0 ? null : [.. added.ExplicitPeers], added.TransferHistory, added.ResumeSeeding,
                added.Bep46FeedUri, added.Bep46Sequence, added.Bep46DownloadRoot));
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
                if (job.ShouldResumeSeeding)
                {
                    await StartJobAsync(job);
                }
                else
                {
                    SaveQueue();
                }
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
                job.AppendLog("verification error: " + exception.Message);
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
            if (OperatingSystem.IsLinux())
            {
                string? linuxSource = await DisplayPromptAsync("Add torrent", "Path to a .torrent file or magnet URI", "Add", "Cancel", "/home/user/file.torrent");
                if (!string.IsNullOrWhiteSpace(linuxSource))
                {
                    await AddSourceAsync(linuxSource);
                }

                return;
            }

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
        string? feedUri = null;
        long? feedSequence = null;
        if (Bep46Link.TryParse(magnetUri, out Bep46Link? feed))
        {
            foreach (TorrentJob job in _jobs)
            {
                if (job.Bep46FeedUri is string subscribedUri &&
                    Bep46StorageLayout.SameFeed(feed!, Bep46Link.Parse(subscribedUri)))
                {
                    _queueView.SelectedItem = job;
                    SelectJob(job);
                    _statusLabel.Text = "Feed already in the library";
                    return;
                }
            }

            feedUri = magnetUri;
            using CancellationTokenSource feedCancellation = new();
            _magnetImportCancellation = feedCancellation;
            SetImportState(true);
            _statusLabel.Text = "Resolving signed torrent feed";
            try
            {
                if (!_settings.EnableDht)
                {
                    throw new InvalidOperationException("BEP 46 magnet links require DHT.");
                }

                Bep46Update update = await feed!.GetCurrentAsync(feedCancellation.Token)
                    ?? throw new InvalidOperationException("No valid BEP 46 update was found in the DHT.");
                feedSequence = update.Sequence;
                magnetUri = feed.ToMagnet(update);
            }
            finally
            {
                _magnetImportCancellation = null;
                SetImportState(false);
            }
        }

        MagnetLink link = MagnetLink.Parse(magnetUri);
        string? feedDownloadRoot = feedUri is null ? null : _settings.DefaultDownloadDirectory;
        string? outputDirectory = feedDownloadRoot is null
            ? null : Bep46StorageLayout.VersionDirectory(feedDownloadRoot, feed!, link.InfoHashHex);
        if (SelectExistingTorrent(link.InfoHashHex, outputDirectory))
        {
            if (feedUri is not null && _selectedJob is not null)
            {
                _selectedJob.Bep46FeedUri = feedUri;
                _selectedJob.Bep46Sequence = Math.Max(_selectedJob.Bep46Sequence ?? 0, feedSequence!.Value);
                _selectedJob.Bep46DownloadRoot = feedDownloadRoot;
                SaveQueue();
            }

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
            await AddCachedTorrentAsync(cachedPath, metadata, link.ExplicitPeers, feedUri, feedSequence,
                outputDirectory, feedDownloadRoot);
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
        _pasteButton.Text = PlatformIcon(importing ? "\uE711" : "\uE8C8");
        string label = importing ? "Cancel magnet import" : "Paste link";
        ToolTipProperties.SetText(_pasteButton, label);
        SemanticProperties.SetDescription(_pasteButton, label);
    }

    private bool SelectExistingTorrent(string infoHashHex, string? outputDirectory = null)
    {
        string directory = outputDirectory ?? _settings.DefaultDownloadDirectory;
        foreach (TorrentJob existing in _jobs)
        {
            if (string.Equals(existing.InfoHashHex, infoHashHex, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.OutputDirectory, directory, StringComparison.OrdinalIgnoreCase))
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

    private async Task AddCachedTorrentAsync(string cachedPath, TorrentMetadata metadata, IReadOnlyList<string>? explicitPeers = null,
        string? feedUri = null, long? feedSequence = null, string? outputDirectory = null, string? feedDownloadRoot = null,
        TorrentJob? transferFeedFrom = null)
    {
        if (SelectExistingTorrent(metadata.InfoHashHex, outputDirectory))
        {
            return;
        }

        TorrentJob job = new(cachedPath, outputDirectory ?? _settings.DefaultDownloadDirectory);
        job.ApplyMetadata(metadata);
        job.Bep46FeedUri = feedUri;
        job.Bep46Sequence = feedSequence;
        job.Bep46DownloadRoot = feedDownloadRoot;
        if (explicitPeers is not null)
        {
            job.ExplicitPeers.AddRange(explicitPeers);
        }
        job.Status = "Ready";
        job.Message = "Ready to start";
        string? previousFeed = transferFeedFrom?.Bep46FeedUri;
        long? previousSequence = transferFeedFrom?.Bep46Sequence;
        string? previousRoot = transferFeedFrom?.Bep46DownloadRoot;
        if (transferFeedFrom is not null)
        {
            transferFeedFrom.Bep46FeedUri = null;
            transferFeedFrom.Bep46Sequence = null;
            transferFeedFrom.Bep46DownloadRoot = null;
        }

        if (!SaveQueue(added: job))
        {
            if (transferFeedFrom is not null)
            {
                transferFeedFrom.Bep46FeedUri = previousFeed;
                transferFeedFrom.Bep46Sequence = previousSequence;
                transferFeedFrom.Bep46DownloadRoot = previousRoot;
            }

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
            if (!job.IsComplete && (!_settings.EnableDht || job.IsPrivate) &&
                (!_settings.EnableTrackers || job.Trackers.Count == 0) &&
                (job.ExplicitPeers.Count == 0 || job.IsPrivate))
            {
                await DisplayAlertAsync("Discovery disabled", job.IsPrivate
                    ? "Private torrents require a tracker; public DHT and explicit peer hints are disabled."
                    : "Enable trackers or DHT, or add a magnet with an explicit peer.", "OK");
                return;
            }

            HashSet<int> occupiedPorts = [];
            foreach (TorrentJob activeJob in _jobs)
            {
                if (activeJob.ActiveListenPort is int port)
                {
                    occupiedPorts.Add(port);
                }
            }

            TorrentClientOptions options = _settings.ToClientOptions(job.TorrentPath, job.OutputDirectory,
                job.HasDataToResume, job.ExplicitPeers, occupiedPorts, seedCompleted: job.IsComplete);
            job.ApplyEffectiveOptions(options);
            CancellationTokenSource cancellation = new();
            TorrentSession? session = null;
            Progress<TorrentSessionProgress> progress = new(snapshot =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (session is null || !job.IsCurrentSession(session) || cancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    bool wasSeeding = job.ResumeSeeding;
                    job.ApplyProgress(snapshot);
                    if (!wasSeeding && job.ResumeSeeding)
                    {
                        SaveQueue();
                    }

                    _statusLabel.Text = $"{job.Name}: {snapshot.Message}";
                });
            });
            session = new TorrentSession(options, job.QueueLog, progress);
            TorrentSession activeSession = session;
            Task runTask = Task.Run(async () =>
            {
                bool completed = false;
                try
                {
                    _ = await activeSession.RunAsync(cancellation.Token);
                    completed = true;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!job.IsCurrentSession(activeSession)) return;
                        job.ResumeSeeding = false;
                        job.Status = "Error";
                        job.Message = exception.Message;
                        job.AppendLog("error: " + exception.Message);
                        _statusLabel.Text = $"{job.Name}: {exception.Message}";
                    });
                }
                finally
                {
                    TorrentTransferSnapshot transfer = activeSession.GetTransferSnapshot();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (!job.IsCurrentSession(activeSession)) return;
                        job.ApplyTransferSnapshot(transfer);
                        job.DetachRun(completed);
                        if (job.Status != "Error")
                        {
                            bool paused = cancellation.IsCancellationRequested || !completed;
                            job.Status = paused ? "Paused" : "Complete";
                            job.Message = paused ? job.IsComplete ? "Seeding paused" : "Paused" : "Completed";
                        }

                        SaveQueue();
                    });
                }
            });

            job.AttachRun(runTask, cancellation, activeSession,
                options.SeedAfterCompletion && options.ListenPort != 0 ? options.ListenPort : null);
            if (ReferenceEquals(job, _selectedJob) && _activeTab == "Overview")
            {
                RefreshDetails();
            }
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

        job.ResumeSeeding = false;
        SaveQueue();
        await job.StopAsync(preserveSeedingIntent: false);
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

        if (!await TorrentFileReveal.RevealAsync(path))
        {
            await DisplayAlertAsync("Location unavailable", "This torrent has no files on disk yet.", "OK");
        }
    }

    private async Task RevealTorrentFileAsync(TorrentJob job, TorrentFileItem file)
    {
        if (!await TorrentFileReveal.RevealAsync(job.ResolveFilePath(file)))
        {
            await DisplayAlertAsync("File unavailable", "This file has not been created on disk yet.", "OK");
        }
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
        _feedRefreshCancellation.Cancel();
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
        _feedRefreshCancellation.Cancel();
        CancelAllVerifications();
        List<Task> stops = CreateStopTasks(includeFeedRefresh: false);
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

        CheckpointTransferHistory();
    }

    private void CheckpointTransferHistory()
    {
        foreach (TorrentJob job in _jobs)
        {
            job.RefreshTransfer();
        }

        SaveQueue();
    }

    private List<Task> CreateStopTasks(bool includeFeedRefresh = true)
    {
        List<Task> stops = [];
        if (_magnetResolutionTask is not null)
        {
            stops.Add(_magnetResolutionTask);
        }

        if (includeFeedRefresh && _feedRefreshTask is not null)
        {
            stops.Add(_feedRefreshTask);
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

    private Button ToolButton(string text, string icon, Func<Task> action, bool primary = false, bool onLightSurface = false, Func<bool>? selected = null)
    {
        Button button = new()
        {
            Text = primary ? PlatformIcon(icon) + "  " + text : PlatformIcon(icon),
            FontFamily = primary ? "Segoe UI Variable" : IconFontFamily,
            FontSize = primary ? 13 : 18,
            CornerRadius = 6,
            HeightRequest = 38,
            MinimumHeightRequest = 38,
            WidthRequest = primary ? 132 : 40,
            MinimumWidthRequest = primary ? 132 : 40,
            Padding = primary ? new Thickness(12, 5) : new Thickness(8, 5),
        };
        ToolTipProperties.SetText(button, text);
        SemanticProperties.SetDescription(button, text);
        button.Clicked += (_, _) => _ = RunUiActionAsync(action);
        AddHoverFeedback(button, hovered =>
        {
            if (primary)
            {
                return (hovered ? StageAccentHover : StageAccent, RailBackground);
            }

            if (onLightSurface)
            {
                return (hovered ? MutedBackground : Colors.Transparent, hovered ? PrimaryColor : TextColor);
            }

            if (selected?.Invoke() == true)
            {
                return (hovered ? RailSelectedHover : RailSelected, Colors.White);
            }

            return (hovered ? RailSelected : Colors.Transparent, Colors.White);
        });
        return button;
    }

    private static void RemoveNativeSearchBorder(Entry entry)
    {
#if WINDOWS
        entry.HandlerChanged += (_, _) =>
        {
            if (entry.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
            {
                // WinUI draws its focused underline from a theme brush even with zero border thickness.
                textBox.Resources["TextControlBorderBrushFocused"] = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                textBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                textBox.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                textBox.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        };
#elif LINUX
        entry.HandlerChanged += (_, _) =>
        {
            if (entry.Handler?.PlatformView is Gtk.Entry gtkEntry)
            {
                gtkEntry.HasFrame = false;
            }
        };
#endif
    }

    private void AddHoverFeedback(Button button, Func<bool, (Color Background, Color Foreground)> colors, bool track = true)
    {
        bool hovered = false;
        void Apply()
        {
            (Color background, Color foreground) = colors(hovered && button.IsEnabled);
            button.BackgroundColor = background;
            button.TextColor = foreground;
        }

#if WINDOWS
        void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            hovered = true;
            Apply();
        }

        void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            hovered = false;
            Apply();
        }

        button.HandlerChanged += (_, _) =>
        {
            if (button.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement nativeButton)
            {
                nativeButton.PointerEntered += OnPointerEntered;
                nativeButton.PointerExited += OnPointerExited;
            }
        };
        button.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is Microsoft.UI.Xaml.UIElement nativeButton)
            {
                nativeButton.PointerEntered -= OnPointerEntered;
                nativeButton.PointerExited -= OnPointerExited;
            }
        };
#else
        PointerGestureRecognizer pointer = new();
        pointer.PointerEntered += (_, _) =>
        {
            hovered = true;
            Apply();
        };
        pointer.PointerExited += (_, _) =>
        {
            hovered = false;
            Apply();
        };
        button.GestureRecognizers.Add(pointer);
#endif
        button.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Button.IsEnabled))
            {
                Apply();
            }
        };
        if (track)
        {
            _buttonFeedback.Add(button, Apply);
        }
        Apply();
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

    private View BuildQueueEmptyView(bool emptyLibrary)
    {
        VerticalStackLayout stack = new()
        {
            Spacing = 12,
            Padding = new Thickness(18, 42),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        stack.Add(new Label
        {
            Text = PlatformIcon(emptyLibrary ? "\uE8B7" : "\uE721"),
            FontFamily = IconFontFamily,
            FontSize = 28,
            TextColor = StageAccent,
            HorizontalTextAlignment = TextAlignment.Center,
        });
        stack.Add(new Label
        {
            Text = emptyLibrary ? "Library is empty" : "No matches",
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
        });
        if (emptyLibrary)
        {
            Button add = new()
            {
                Text = "Add torrent",
                FontSize = 13,
                CornerRadius = 6,
                BackgroundColor = StageAccent,
                TextColor = RailBackground,
                Padding = new Thickness(16, 7),
            };
            add.Clicked += (_, _) => _ = RunUiActionAsync(AddTorrentAsync);
            AddHoverFeedback(add, hovered => (hovered ? StageAccentHover : StageAccent, RailBackground), track: false);
            stack.Add(add);
        }
        else
        {
            Button showAll = new()
            {
                Text = "Show all",
                FontSize = 13,
                CornerRadius = 6,
                BackgroundColor = RailSurface,
                TextColor = StageAccent,
                Padding = new Thickness(16, 7),
            };
            showAll.Clicked += (_, _) =>
            {
                _searchEntry.Text = string.Empty;
                SetQueueFilter("All");
            };
            AddHoverFeedback(showAll, hovered => (hovered ? RailSelected : RailSurface, StageAccent), track: false);
            stack.Add(showAll);
        }

        return stack;
    }

    private View BuildDetailEmptyView()
    {
        VerticalStackLayout stack = new()
        {
            Spacing = 16,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        stack.Add(new Label
        {
            Text = PlatformIcon("\uE8B7"),
            FontFamily = IconFontFamily,
            FontSize = 40,
            TextColor = PrimaryColor,
            HorizontalTextAlignment = TextAlignment.Center,
        });
        stack.Add(new Label
        {
            Text = _jobs.Count == 0 ? "Your library is ready" : "Select a torrent",
            FontSize = 22,
            FontAttributes = FontAttributes.Bold,
            TextColor = TextColor,
            HorizontalTextAlignment = TextAlignment.Center,
        });
        if (_jobs.Count == 0)
        {
            HorizontalStackLayout actions = new() { Spacing = 8, HorizontalOptions = LayoutOptions.Center };
            Button add = new() { Text = "Add torrent", FontSize = 13, CornerRadius = 6, BackgroundColor = PrimaryColor, TextColor = Colors.White, Padding = new Thickness(16, 8) };
            add.Clicked += (_, _) => _ = RunUiActionAsync(AddTorrentAsync);
            AddHoverFeedback(add, hovered => (hovered ? PrimaryHover : PrimaryColor, Colors.White), track: false);
            Button paste = new() { Text = "Paste link", FontSize = 13, CornerRadius = 6, BackgroundColor = MutedBackground, TextColor = TextColor, Padding = new Thickness(16, 8) };
            paste.Clicked += (_, _) => _ = RunUiActionAsync(PasteLinkAsync);
            AddHoverFeedback(paste, hovered => (hovered ? BorderColor : MutedBackground, TextColor), track: false);
            actions.Add(add);
            actions.Add(paste);
            stack.Add(actions);
        }

        return stack;
    }

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

    private static View BoundInfo(string label, string bindingPath, string? tooltipPath = null)
    {
        Grid grid = InfoRowBase(label);
        Label value = SmallLabel(string.Empty);
        value.TextColor = TextColor;
        value.HorizontalTextAlignment = TextAlignment.End;
        value.LineBreakMode = tooltipPath is null ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
        if (tooltipPath is not null)
        {
            value.MaxLines = 1;
            value.SetBinding(ToolTipProperties.TextProperty, tooltipPath);
        }
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
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 8,
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
