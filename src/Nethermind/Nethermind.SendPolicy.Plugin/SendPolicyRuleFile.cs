// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// Serves the current rules from <see cref="ISendPolicyConfig.RulesPath"/>.
/// </summary>
/// <remarks>
/// The file must be usable when the node starts. It is parsed again whenever its write time or length changes,
/// so an edit takes effect on the next send without a restart. Safe to call from concurrent JSON-RPC requests.
/// </remarks>
public sealed class SendPolicyRuleFile
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _reloadLock = new();
    private volatile Loaded? _loaded;

    /// <exception cref="InvalidConfigurationException">The path is not set, or the file cannot be read or parsed.</exception>
    public SendPolicyRuleFile(ISendPolicyConfig config, ILogManager logManager)
    {
        if (string.IsNullOrWhiteSpace(config.RulesPath))
            throw new InvalidConfigurationException($"{nameof(ISendPolicyConfig.RulesPath)} must be set when SendPolicy is enabled.", ExitCodes.ConflictingConfigurations);
        _path = config.RulesPath;
        _logger = logManager.GetClassLogger<SendPolicyRuleFile>();
        if (Current.Error is { } error)
            throw new InvalidConfigurationException($"SendPolicy rules at {_path}: {error}", ExitCodes.ConflictingConfigurations);
    }

    public SendPolicyRules Current
    {
        get
        {
            FileStamp stamp = FileStamp.Of(_path);
            Loaded? loaded = _loaded;
            if (loaded is not null && loaded.Stamp == stamp) return loaded.Rules;
            lock (_reloadLock)
            {
                loaded = _loaded;
                if (loaded is not null && loaded.Stamp == stamp) return loaded.Rules;
                SendPolicyRules rules = Read(loaded?.LastGood ?? SendPolicyRules.Empty);
                _loaded = new Loaded(stamp, rules, rules.Error is null ? rules : loaded?.LastGood ?? SendPolicyRules.Empty);
                return rules;
            }
        }
    }

    private SendPolicyRules Read(SendPolicyRules lastGood)
    {
        SendPolicyRules rules;
        try
        {
            rules = SendPolicyRules.Parse(File.ReadAllLines(_path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            rules = SendPolicyRules.Unusable(lastGood, $"rule file unusable ({e.Message})");
        }

        if (rules.Error is not null)
        {
            if (_logger.IsError) _logger.Error($"SendPolicy rules at {_path}: {rules.Error}; refusing every transaction from {rules.GuardedSenders.Count} guarded sender(s)");
        }
        else if (_logger.IsInfo)
        {
            _logger.Info($"SendPolicy rules loaded from {_path}: {rules.GuardedSenders.Count} guarded sender(s), {rules.Destinations.Count} destination(s), {rules.Grants.Count} grant(s), fee ceiling {rules.MaxFee?.ToString() ?? "none"}");
        }

        return rules;
    }

    private sealed record Loaded(FileStamp Stamp, SendPolicyRules Rules, SendPolicyRules LastGood);

    private readonly record struct FileStamp(bool Exists, DateTime WriteTimeUtc, long Length)
    {
        public static FileStamp Of(string path)
        {
            FileInfo info = new(path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
        }
    }
}
