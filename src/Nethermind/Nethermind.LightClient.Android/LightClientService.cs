// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Android.Content;
using Android.Content.PM;
using Android.OS;
using Nethermind.LightClient.Mobile;

namespace Nethermind.LightClient.Android;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class LightClientService : Service
{
    internal const string StopAction = "io.nethermind.lightclient.STOP";
    private const string ChannelId = "mainnet-sync";
    private static readonly SemaphoreSlim Session = new(1, 1);
    private static MobileProgress _progress = new("Stopped");
    private static int _runningClients;
    private readonly Lock _publication = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;
    private Handler? _handler;
    private bool _stopping;
    private bool _finished;
    private bool _destroyed;
    private bool _restartRequested;
    private int _lastStartId;
    internal static MobileProgress Progress => Volatile.Read(ref _progress);
    internal static bool IsRunning => _runningClients != 0;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _lastStartId = startId;
        if (intent?.Action == StopAction)
        {
            _restartRequested = false;
            RequestStop();
            if (_worker is null) StopSelf(startId);
            return StartCommandResult.NotSticky;
        }
        if (_worker is not null)
        {
            if (_stopping) _restartRequested = true;
            return StartCommandResult.NotSticky;
        }
        StartClient();
        return StartCommandResult.NotSticky;
    }

    private void StartClient()
    {
        MobileTrackingSettings? settings = TrackingPreferences.Load(this);
        if (settings is null)
        {
            StopSelf(_lastStartId);
            return;
        }
        _handler ??= new(Looper.MainLooper!);
        lock (_publication)
        {
            _stopping = false;
            _finished = false;
            Volatile.Write(ref _progress, new("Starting", Settings: settings));
        }
        NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
        using NotificationChannel channel = new(ChannelId, "Mainnet verification", NotificationImportance.Low);
        manager.CreateNotificationChannel(channel);
        using PendingIntent open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable)!;
        using Notification notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Nethermind LC · Mainnet")!
            .SetContentText("Verifying the chain and tracking account balances")!
            .SetSmallIcon(Resource.Drawable.lc_icon)!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .Build();
        StartForeground(1, notification);
        _runningClients++;
        _stop = new();
        CancellationToken token = _stop.Token;
        string directory = Path.Combine(FilesDir!.AbsolutePath, "mainnet");
        _worker = Task.Run(async () =>
        {
            bool entered = false;
            try
            {
                await Session.WaitAsync(token);
                entered = true;
                await MobileClient.RunAsync(directory, settings, progress =>
                {
                    lock (_publication)
                        if (!_stopping) Volatile.Write(ref _progress, progress);
                }, new AndroidLogManager(), token);
            }
            catch (System.OperationCanceledException) when (token.IsCancellationRequested)
            {
                FinishProgress("Stopped", null);
            }
            catch (Exception error)
            {
                global::Android.Util.Log.Error("NethermindLC", error.ToString());
                FinishProgress("Stopped after error", error.GetBaseException().Message);
            }
            finally
            {
                if (entered) Session.Release();
                _handler.Post(FinishRun);
            }
        });
    }

    private void FinishProgress(string stage, string? error)
    {
        lock (_publication)
        {
            _finished = true;
            Volatile.Write(ref _progress, Progress with { Stage = stage, Error = error, HeadFresh = false, BalanceAtHead = false });
        }
    }

    // Service commands and this completion callback run on the main looper; a restart waits for all old owners to close.
    private void FinishRun()
    {
        _runningClients--;
        _worker = null;
        _stop?.Dispose();
        _stop = null;
        if (_restartRequested && !_destroyed)
        {
            _restartRequested = false;
            StartClient();
            return;
        }
        StopForeground(StopForegroundFlags.Remove);
        if (_destroyed)
        {
            _handler?.Dispose();
            _handler = null;
        }
        else StopSelf(_lastStartId);
    }

    private void RequestStop()
    {
        lock (_publication)
        {
            _stopping = true;
            if (!_finished)
            {
                _finished = _worker is null;
                Volatile.Write(ref _progress, Progress with { Stage = _finished ? "Stopped" : "Stopping", HeadFresh = false, BalanceAtHead = false });
            }
        }
        _stop?.Cancel();
    }

    public override void OnTimeout(int startId, ForegroundService foregroundServiceType)
    {
        _destroyed = true;
        _restartRequested = false;
        RequestStop();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf(startId);
    }

    public override void OnDestroy()
    {
        _destroyed = true;
        _restartRequested = false;
        RequestStop();
        if (_worker is null)
        {
            _stop?.Dispose();
            _stop = null;
            _handler?.Dispose();
            _handler = null;
        }
        base.OnDestroy();
    }
}
