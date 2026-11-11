// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Nethermind.LightClient.Mobile;

namespace Nethermind.LightClient.Android;

[Activity(Label = "Network & bootstrap", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class NetworkDetailsActivity : Activity
{
    private TextView _status = null!;
    private TextView _details = null!;
    private TextView _error = null!;
    private Handler? _handler;
    private Action? _refresh;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ScrollView scroll = new(this);
        scroll.SetOnApplyWindowInsetsListener(new ScreenInsets());
        LinearLayout content = new(this) { Orientation = Orientation.Vertical };
        int padding = (int)(24 * Resources!.DisplayMetrics!.Density);
        content.SetPadding(padding, padding, padding, padding);
        scroll.AddView(content);
        Text(content, "Network & bootstrap", 27, Color.Rgb(0, 179, 255));
        Text(content, "ETHEREUM MAINNET", 12, Color.Rgb(255, 153, 0));
        Button back = new(this) { Text = "Back" };
        back.Click += (_, _) => Finish();
        content.AddView(back);
        _status = Text(content, "", 20, Color.Rgb(255, 153, 0));
        _details = Text(content, "", 16, Color.White);
        _error = Text(content, "", 14, Color.Rgb(255, 153, 0));
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
        _handler = new(Looper.MainLooper!);
        _refresh = () =>
        {
            Render();
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

    private TextView Text(LinearLayout content, string text, float size, Color color)
    {
        TextView view = new(this) { Text = text, TextSize = size };
        view.SetTextColor(color);
        view.SetPadding(0, 8, 0, 8);
        content.AddView(view);
        return view;
    }
}
