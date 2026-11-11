// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Log = Android.Util.Log;

namespace Nethermind.LightClient.Android;

internal sealed class AndroidLogManager : ILogManager
{
    public ILogger GetLogger(string loggerName) => new(new AndroidLogger(loggerName));
    public ILogger GetClassLogger<T>() => GetLogger(ILogManager.GetLoggerName(typeof(T)));

    private sealed class AndroidLogger(string category) : InterfaceLogger
    {
        public bool IsInfo => category.StartsWith("LightClient", StringComparison.Ordinal);
        public bool IsWarn => true;
        public bool IsError => true;
        public bool IsDebug => false;
        public bool IsTrace => false;
        public void Info(string text) => Log.Info("NethermindLC", $"{category}: {text}");
        public void Warn(string text) => Log.Warn("NethermindLC", $"{category}: {text}");
        public void Error(string text, Exception? ex = null) => Log.Error("NethermindLC", $"{category}: {text} {ex}");
        public void Debug(string text) => Log.Debug("NethermindLC", $"{category}: {text}");
        public void Trace(string text) => Log.Verbose("NethermindLC", $"{category}: {text}");
    }
}
