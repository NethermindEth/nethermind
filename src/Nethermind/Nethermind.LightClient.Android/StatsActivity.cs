// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Util;
using Android.Views;
using Nethermind.LightClient.Mobile;
using ColorStateList = Android.Content.Res.ColorStateList;

namespace Nethermind.LightClient.Android;

[Activity(Label = "Client stats", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class StatsActivity : Activity
{
    private TextView _status = null!;
    private TextView _details = null!;
    private TextView _error = null!;
    private TextView _usage = null!;
    private Handler? _handler;
    private Action? _refresh;
    private Task<string>? _sample;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ScrollView scroll = new(this);
        scroll.SetOnApplyWindowInsetsListener(new ScreenInsets());
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        int padding = (int)(24 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);
        scroll.AddView(content);
        LinearLayout header = new(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        content.AddView(header);
        TextView title = Text(header, "Client stats", 25, Color.Rgb(0, 179, 255));
        title.SetSingleLine(true);
        title.SetHorizontallyScrolling(false);
        title.SetAutoSizeTextTypeUniformWithConfiguration(16, 25, 1, (int)ComplexUnitType.Sp);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, Dp(48), 1);
        title.Gravity = GravityFlags.CenterVertical;
        ImageButton back = new(this) { ContentDescription = "Back to balances" };
        back.SetImageResource(Resource.Drawable.back_icon);
        back.Background = new RippleDrawable(ColorStateList.ValueOf(Color.Argb(45, 255, 255, 255)), null, null);
        back.SetPadding(Dp(24), Dp(12), 0, Dp(12));
        back.SetScaleType(ImageView.ScaleType.FitCenter);
        header.AddView(back, new LinearLayout.LayoutParams(Dp(48), Dp(48)) { MarginStart = Dp(8) });
        back.Click += (_, _) => Finish();
        Text(content, "ETHEREUM MAINNET", 12, Color.Rgb(255, 153, 0));
        _status = Text(content, "", 20, Color.Rgb(255, 153, 0));
        _details = Text(content, "", 16, Color.White);
        _error = Text(content, "", 14, Color.Rgb(255, 153, 0));
        Text(content, "\nApp usage", 18, Color.White);
        _usage = Text(content, "Measuring…", 16, Color.White);
        Text(content, "Updated every 5 seconds while this screen is open.", 12, Color.LightGray);
        Text(content, "\nCheckpoint provider", 18, Color.White);
        Text(content, MobileClient.CheckpointUrl, 14, Color.LightGray).SetTextIsSelectable(true);
        Text(content, "Bootstrap trusts a recent finalized beacon checkpoint from this provider. " +
            "After bootstrap, consensus updates and execution state come directly from P2P peers. " +
            "Sync committee signatures and Merkle proofs are verified on this device. " +
            "Latest heads are optimistic and may reorganize; finalized blocks have stronger confirmation.", 14, Color.LightGray);
        SetContentView(scroll);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _usage.Text = "Measuring…";
        _handler = new(Looper.MainLooper!);
        ResourceUsage usage = new();
        long nextSample = 0;
        _refresh = () =>
        {
            Render();
            if (_sample is { IsCompletedSuccessfully: true })
            {
                _usage.Text = _sample.GetAwaiter().GetResult();
                _sample = null;
            }
            long now = SystemClock.ElapsedRealtime();
            if (_sample is null && now >= nextSample)
            {
                _sample = Task.Run(usage.Sample);
                nextSample = now + 5000;
            }
            _handler?.PostDelayed(_refresh!, 1000);
        };
        _handler.Post(_refresh);
    }

    protected override void OnPause()
    {
        _handler?.RemoveCallbacksAndMessages(null);
        _handler?.Dispose();
        _handler = null;
        _refresh = null;
        // Detached samples retain only ResourceUsage, not this activity or its views.
        _sample = null;
        base.OnPause();
    }

    private void Render()
    {
        MobileTrackingSettings? settings = TrackingPreferences.Load(this);
        MobileProgress progress = LightClientService.Progress;
        if (settings is null || progress.Settings != settings)
            progress = new(settings is null ? "No account configured" : LightClientService.IsRunning ? "Waiting for current session" : "Stopped",
                Settings: settings);
        _status.Text = progress.StatusText;
        _status.SetTextColor(progress.IsSynced ? Color.Rgb(65, 211, 139) : Color.Rgb(255, 153, 0));
        _details.Text = $"Stage  {progress.Stage}\n\n" +
            $"Peers  CL {progress.BeaconPeers}  ·  EL {progress.ExecutionPeers}  ·  SNAP {progress.SnapPeers}\n\n" +
            $"Latest block  {progress.HeadNumber?.ToString() ?? "waiting"}\n" +
            $"Beacon slot  {progress.HeadSlot?.ToString() ?? "waiting"}\n" +
            $"Head freshness  {(progress.HeadFresh ? "fresh" : "waiting for fresh head")}\n" +
            $"Finalized block  {progress.FinalizedNumber?.ToString() ?? "waiting"}";
        _error.Text = progress.Error ?? "";
    }

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private TextView Text(LinearLayout content, string text, float size, Color color)
    {
        TextView view = new(this) { Text = text, TextSize = size };
        view.SetTextColor(color);
        view.SetPadding(0, Dp(4), 0, Dp(4));
        content.AddView(view);
        return view;
    }
}
