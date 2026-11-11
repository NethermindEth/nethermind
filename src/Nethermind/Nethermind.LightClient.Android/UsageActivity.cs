// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Content.PM;
using Android.Graphics;
using Android.OS;

namespace Nethermind.LightClient.Android;

/// <summary>Displays the app's RAM, CPU and network traffic statistics.</summary>
[Activity(Label = "App usage", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class UsageActivity : Activity
{
    private TextView _details = null!;
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
        Text(content, "App usage", 27, Color.Rgb(0, 179, 255));
        Text(content, "RAM · CPU · NETWORK TRAFFIC", 12, Color.Rgb(255, 153, 0));
        Button back = new(this) { Text = "Back" };
        back.Click += (_, _) => Finish();
        content.AddView(back);
        _details = Text(content, "Measuring…", 16, Color.White);
        Text(content, "Updated every 5 seconds while this screen is open.", 14, Color.LightGray);
        SetContentView(scroll);
    }

    protected override void OnResume()
    {
        base.OnResume();
        _details.Text = "Measuring…";
        _handler = new(Looper.MainLooper!);
        ResourceUsage usage = new();
        long nextSample = 0;
        _refresh = () =>
        {
            if (_sample is { IsCompletedSuccessfully: true })
            {
                _details.Text = _sample.GetAwaiter().GetResult();
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
        // A running sample owns only ResourceUsage, so it can finish without retaining the screen.
        _sample = null;
        base.OnPause();
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
