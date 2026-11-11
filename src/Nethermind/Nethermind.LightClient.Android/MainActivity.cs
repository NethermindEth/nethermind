// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Nethermind.LightClient.Mobile;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Nethermind.LightClient.Android;

[Activity(Label = "Nethermind LC", MainLauncher = true, Exported = true, ScreenOrientation = ScreenOrientation.Portrait, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : Activity
{
    private static readonly Color Muted = Color.Rgb(156, 174, 189);
    private static readonly Color Ready = Color.Rgb(65, 211, 139);
    private static readonly Color Pending = Color.Rgb(255, 183, 77);
    private TextView _status = null!;
    private TextView _error = null!;
    private TextView _balanceStatus = null!;
    private readonly Dictionary<string, (LinearLayout Card, TextView Amount)> _assets = [];
    private AlertDialog? _proofDialog;
    private TextView? _proofText;
    private string? _proofSymbol;
    private PopupMenu? _menu;
    private MobileTrackingSettings? _settings;
    private EditText? _accountInput;
    private CheckBox? _usdt;
    private CheckBox? _dai;
    private CheckBox? _usdc;
    private AlertDialog? _resetDialog;
    private Handler? _handler;
    private Action? _refresh;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _settings = TrackingPreferences.Load(this);
        if (_settings is null) ShowSetup(savedInstanceState);
        else
        {
            ShowDashboard();
            if (savedInstanceState is null) StartClient();
        }
    }

    private LinearLayout CreateContent(bool showMenu = false)
    {
        ScrollView scroll = new(this) { FillViewport = true };
        scroll.SetOnApplyWindowInsetsListener(new ScreenInsets());
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        int padding = (int)(24 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);
        scroll.AddView(content);
        LinearLayout header = new(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        content.AddView(header);
        TextView title = Text(header, "NETHERMIND  LC", 25, Color.Rgb(0, 179, 255));
        title.SetSingleLine(true);
        title.SetAutoSizeTextTypeUniformWithConfiguration(16, 25, 1, (int)ComplexUnitType.Sp);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(48), 1);
        title.Gravity = GravityFlags.CenterVertical;
        if (showMenu)
        {
            Button menu = new(this) { Text = "\u2630", TextSize = 25, ContentDescription = "Menu" };
            menu.SetTextColor(Color.White);
            menu.SetPadding(0, 0, 0, 0);
            menu.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(45, 255, 255, 255)), null, null);
            header.AddView(menu, new LinearLayout.LayoutParams(Dp(48), Dp(48)) { MarginStart = Dp(8) });
            menu.Click += (_, _) => ShowMenu(menu);
        }
        Text(content, "ETHEREUM MAINNET  ·  ON-DEVICE VERIFICATION", 12, Color.Rgb(255, 153, 0));
        SetContentView(scroll);
        return content;
    }

    private void ShowSetup(Bundle? savedInstanceState = null)
    {
        _assets.Clear();
        LinearLayout content = CreateContent();
        Text(content, "\nTrack an account", 24, Color.White);
        Text(content, "Enter an Ethereum address. ETH is always included; choose the tokens to track too.", 15, Color.LightGray);
        EditText accountInput = new(this)
        {
            Hint = "0x… Ethereum address",
            Text = savedInstanceState?.GetString("setup-account") ?? "",
            InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions,
        };
        _accountInput = accountInput;
        accountInput.SetSingleLine(true);
        accountInput.SetTextColor(Color.White);
        accountInput.SetHintTextColor(Color.Gray);
        content.AddView(accountInput);
        Text(content, "\nTokens", 18, Color.White);
        CheckBox usdt = _usdt = Token("USDT · Tether USD", savedInstanceState?.GetBoolean("setup-usdt", true) ?? true);
        CheckBox dai = _dai = Token("DAI · Dai", savedInstanceState?.GetBoolean("setup-dai", true) ?? true);
        CheckBox usdc = _usdc = Token("USDC · USD Coin", savedInstanceState?.GetBoolean("setup-usdc", true) ?? true);
        TextView error = Text(content, "", 14, Color.Rgb(255, 153, 0));
        Button save = new(this) { Text = "Save and start" };
        save.Click += (_, _) =>
        {
            if (!MobileTrackingSettings.TryCreate(accountInput.Text, usdt.Checked, dai.Checked, usdc.Checked,
                    out MobileTrackingSettings? settings))
            {
                error.Text = "Enter 0x followed by 40 hexadecimal characters. Mixed-case addresses must have a valid checksum.";
                return;
            }
            save.Enabled = false;
            if (!TrackingPreferences.Save(this, settings!))
            {
                save.Enabled = true;
                error.Text = "Could not save settings. Please try again.";
                return;
            }
            _settings = settings;
            ShowDashboard();
            StartClient();
        };
        content.AddView(save);
        Text(content, "\nOnly public balances are read. No wallet connection or private key is needed.", 13, Color.LightGray);

        CheckBox Token(string label, bool selected)
        {
            CheckBox checkbox = new(this) { Text = label, Checked = selected };
            checkbox.SetTextColor(Color.White);
            content.AddView(checkbox);
            return checkbox;
        }
    }

    private void ShowDashboard()
    {
        _accountInput = null;
        _usdt = _dai = _usdc = null;
        LinearLayout content = CreateContent(showMenu: true);
        _assets.Clear();
        _status = Text(content, "Starting", 16, Pending);
        Text(content, "\nTRACKED ACCOUNT", 11, Muted);
        string address = _settings!.AccountAddress;
        TextView account = Text(content, $"{address[..8]}\u2026{address[^6..]}", 14, Color.White);
        account.ContentDescription = address;
        account.SetSingleLine(true);
        account.Ellipsize = TextUtils.TruncateAt.Middle;
        LinearLayout balances = new(this) { Orientation = Orientation.Horizontal };
        balances.SetGravity(GravityFlags.CenterVertical);
        balances.SetPadding(0, Dp(20), 0, Dp(8));
        content.AddView(balances);
        TextView heading = Text(balances, "Balances", 23, Color.White);
        heading.SetTypeface(null, TypefaceStyle.Bold);
        heading.SetSingleLine(true);
        heading.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        _balanceStatus = Text(balances, "Waiting", 12, Muted);
        _balanceStatus.Gravity = GravityFlags.End;
        _balanceStatus.SetSingleLine(true);
        AddAsset(content, "ETH", "Ξ", Color.Rgb(139, 165, 255));
        if (_settings.Usdt) AddAsset(content, "USDT", "₮", Color.Rgb(68, 205, 167));
        if (_settings.Dai) AddAsset(content, "DAI", "D", Color.Rgb(255, 187, 70));
        if (_settings.Usdc) AddAsset(content, "USDC", "$", Color.Rgb(83, 169, 255));
        _error = Text(content, "", 14, Pending);
        Render(LightClientService.Progress);
    }

    private void ShowMenu(View anchor)
    {
        if (_menu is not null) return;
        PopupMenu menu = _menu = new(this, anchor);
        menu.Inflate(Resource.Menu.navigation);
        UpdateMenu();
        menu.MenuItemClick += (_, args) =>
        {
            switch (args.Item!.ItemId)
            {
                case Resource.Id.menu_network:
                    StartActivity(new Intent(this, typeof(NetworkDetailsActivity)));
                    break;
                case Resource.Id.menu_usage:
                    StartActivity(new Intent(this, typeof(UsageActivity)));
                    break;
                case Resource.Id.menu_start:
                    StartClient();
                    break;
                case Resource.Id.menu_stop:
                    StartService(new Intent(this, typeof(LightClientService)).SetAction(LightClientService.StopAction));
                    break;
                case Resource.Id.menu_reset:
                    ConfirmReset();
                    break;
            }
            args.Handled = true;
        };
        menu.DismissEvent += (_, _) =>
        {
            _menu = null;
            menu.Dispose();
        };
        menu.Show();
    }

    private void UpdateMenu()
    {
        bool running = LightClientService.IsRunning;
        _menu?.Menu?.FindItem(Resource.Id.menu_start)?.SetVisible(!running);
        _menu?.Menu?.FindItem(Resource.Id.menu_stop)?.SetVisible(running);
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private void AddAsset(LinearLayout content, string symbol, string glyph, Color accent)
    {
        LinearLayout card = new(this) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        card.SetGravity(GravityFlags.CenterVertical);
        card.SetMinimumHeight(Dp(64));
        card.SetPadding(0, Dp(6), 0, Dp(6));
        card.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(45, 255, 255, 255)), null, null);
        content.AddView(card, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        TextView icon = new(this)
        {
            Text = glyph,
            TextSize = 22,
            Gravity = GravityFlags.Center,
            ImportantForAccessibility = ImportantForAccessibility.No
        };
        icon.SetTextColor(accent);
        GradientDrawable circle = new();
        circle.SetShape(ShapeType.Oval);
        circle.SetColor(Color.Argb(32, accent.R, accent.G, accent.B));
        icon.Background = circle;
        LinearLayout.LayoutParams iconLayout = new(Dp(36), Dp(36));
        iconLayout.MarginEnd = Dp(10);
        card.AddView(icon, iconLayout);

        TextView amount = new(this)
        {
            Text = "\u2014",
            TextSize = 22,
            Gravity = GravityFlags.End | GravityFlags.CenterVertical,
            ImportantForAccessibility = ImportantForAccessibility.No
        };
        amount.SetTextColor(Color.White);
        card.AddView(amount, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        amount.SetSingleLine(true);
        amount.Ellipsize = TextUtils.TruncateAt.End;
        amount.SetAutoSizeTextTypeUniformWithConfiguration(12, 22, 1, (int)ComplexUnitType.Sp);
        card.Click += (_, _) => ShowProof(symbol);
        _assets.Add(symbol, (card, amount));
    }

    private void ShowProof(string symbol)
    {
        if (_proofDialog is not null) return;
        ScrollView scroll = new(this);
        scroll.SetPadding(Dp(24), Dp(8), Dp(24), Dp(8));
        _proofSymbol = symbol;
        _proofText = new(this) { TextSize = 14 };
        _proofText.SetTextColor(Color.White);
        _proofText.SetTextIsSelectable(true);
        scroll.AddView(_proofText);
        using AlertDialog.Builder builder = new(this);
        builder.SetTitle($"{symbol} verification")!.SetView(scroll)!.SetPositiveButton("Close", (_, _) => { });
        AlertDialog dialog = _proofDialog = builder.Create()!;
        dialog.DismissEvent += (_, _) =>
        {
            _proofDialog = null;
            _proofText = null;
            _proofSymbol = null;
            dialog.Dispose();
        };
        Render(LightClientService.Progress);
        dialog.Show();
    }

    private void ConfirmReset()
    {
        if (_resetDialog is not null) return;
        using AlertDialog.Builder builder = new(this);
        builder
            .SetTitle("Reset tracking settings?")!
            .SetMessage("The client will stop and forget the account and token selection. Verified chain data will be kept.")!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Reset", (_, _) => ResetSettings());
        AlertDialog dialog = _resetDialog = builder.Create()!;
        dialog.DismissEvent += (_, _) =>
        {
            _resetDialog = null;
            dialog.Dispose();
        };
        dialog.Show();
    }

    protected override void OnDestroy()
    {
        _menu?.Dismiss();
        _resetDialog?.Dismiss();
        _proofDialog?.Dismiss();
        base.OnDestroy();
    }

    private void ResetSettings()
    {
        if (!TrackingPreferences.Clear(this))
        {
            _error.Text = "Could not reset settings. Please try again.";
            return;
        }
        StartService(new Intent(this, typeof(LightClientService)).SetAction(LightClientService.StopAction));
        _proofDialog?.Dismiss();
        _settings = null;
        ShowSetup();
    }

    private TextView Text(LinearLayout content, string text, float size, Color color)
    {
        TextView view = new(this) { Text = text, TextSize = size };
        view.SetTextColor(color);
        view.SetPadding(0, Dp(4), 0, Dp(4));
        content.AddView(view);
        return view;
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        if (_settings is null && _accountInput is not null)
        {
            outState.PutString("setup-account", _accountInput.Text);
            outState.PutBoolean("setup-usdt", _usdt!.Checked);
            outState.PutBoolean("setup-dai", _dai!.Checked);
            outState.PutBoolean("setup-usdc", _usdc!.Checked);
        }
        base.OnSaveInstanceState(outState);
    }

    private void StartClient()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        StartForegroundService(new Intent(this, typeof(LightClientService)));
        _menu?.Menu?.FindItem(Resource.Id.menu_start)?.SetVisible(false);
    }

    protected override void OnResume()
    {
        base.OnResume();
        MobileTrackingSettings? settings = TrackingPreferences.Load(this);
        if (_settings != settings)
        {
            _resetDialog?.Dismiss();
            _proofDialog?.Dismiss();
            _settings = settings;
            if (settings is null) ShowSetup();
            else ShowDashboard();
        }
        _handler = new(Looper.MainLooper!);
        _refresh = () =>
        {
            if (_settings is null)
            {
                _handler?.PostDelayed(_refresh!, 1000);
                return;
            }
            Render(LightClientService.Progress);
            _handler?.PostDelayed(_refresh!, 1000);
        };
        _handler.Post(_refresh);
    }

    protected override void OnPause()
    {
        _menu?.Dismiss();
        _handler?.RemoveCallbacksAndMessages(null);
        _handler?.Dispose();
        _handler = null;
        _refresh = null;
        base.OnPause();
    }

    private void Render(MobileProgress progress)
    {
        if (progress.Settings != _settings)
            progress = new(LightClientService.IsRunning ? "Starting" : "Stopped", Settings: _settings);
        _status.Text = progress.StatusText;
        _status.SetTextColor(progress.IsSynced ? Ready : Pending);
        UpdateMenu();
        bool hasBalance = false;
        bool hasError = progress.Error is not null;
        foreach ((string symbol, (LinearLayout card, TextView amount)) in _assets)
        {
            MobileTokenBalance? balance = AssetBalance(progress, symbol);
            bool current = balance is not null && progress.IsCurrent(balance);
            string status = balance switch
            {
                { Units: null, Error: not null } => "Unavailable",
                null or { Units: null } => "Waiting",
                { Error: not null } => "Update failed",
                _ when current => "Verified",
                _ => LightClientService.IsRunning ? "Refreshing" : "Last verified",
            };
            amount.Text = balance?.Units is null ? "—" : balance.AmountText;
            amount.SetTextColor(current ? Color.White : balance?.Error is not null ? Pending : Muted);
            hasBalance |= balance?.Units is not null;
            hasError |= balance?.Error is not null;
            card.ContentDescription = $"{symbol}, {balance?.AmountText ?? "Waiting for verified state"}, {status}. Show verification details";
        }
        _balanceStatus.Text = progress.IsSynced ? "Verified" : hasError ? "Update failed" : !hasBalance ? "Waiting"
            : LightClientService.IsRunning ? "Refreshing" : "Last verified";
        _balanceStatus.SetTextColor(progress.IsSynced ? Ready : hasError ? Pending : Muted);
        _error.Text = progress.Error is null ? "" : "Updates unavailable. See Network details.";
        _error.Visibility = progress.Error is null ? ViewStates.Gone : ViewStates.Visible;
        if (_proofText is not null && _proofSymbol is { } selected)
        {
            MobileTokenBalance? proof = AssetBalance(progress, selected);
            string details = proof?.Units is null ? "No verified balance yet."
                : $"{proof.AmountText}\n{(progress.IsCurrent(proof) ? "Matches the fresh authenticated head" : "Last verified balance; not confirmed at the current head")}\n\n" +
                    $"{proof.DetailsText}\nVerified {proof.VerifiedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}\n" +
                    $"{(selected == "ETH" ? "SNAP request" : "Local balanceOf")}  {proof.RequestMilliseconds:F1} ms\n\n" +
                    $"Block hash\n{proof.BlockHash}\n\nState root\n{proof.StateRoot}\n\n" +
                    $"Exact {(selected == "ETH" ? "wei" : "base units")}\n{proof.Units}\nDecimals  {proof.Decimals}";
            string text = $"Account\n{_settings!.AccountAddress}\n\n{details}" +
                (proof is { Units: null, Error: { } error } ? $"\n\nRefresh unavailable\n{error}" : "");
            if (_proofText.Text != text) _proofText.Text = text;
        }
    }

    private static MobileTokenBalance? AssetBalance(MobileProgress progress, string symbol)
    {
        if (symbol == "ETH")
        {
            MobileBalance? eth = progress.Balance;
            return new("ETH", 18, eth?.Wei, eth?.BlockNumber, eth?.BlockHash, eth?.StateRoot,
                eth?.VerifiedAt, eth?.RequestMilliseconds, progress.Error);
        }
        if (progress.Tokens is not null)
            foreach (MobileTokenBalance token in progress.Tokens)
                if (token.Symbol == symbol) return token;
        return null;
    }
}
