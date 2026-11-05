// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.Abstractions;
using System.Text;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;

namespace Nethermind.SendPolicy.Plugin;

/// <summary>
/// Serves the current rules from <see cref="ISendPolicyConfig.RulesPath"/>.
/// </summary>
/// <remarks>
/// The file must be usable when the node starts. Its content is checked on every send, so even a same-size
/// replacement that preserves the file timestamp takes effect without a restart. Safe to call from concurrent JSON-RPC requests.
/// Update rules by writing and closing a temporary file in the same directory, then atomically replacing this file.
/// In-place writes can expose an incomplete but syntactically valid policy.
/// </remarks>
public sealed class SendPolicyRuleFile
{
    private readonly string _path;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly Lock _reloadLock = new();
    private volatile Loaded? _loaded;

    /// <exception cref="InvalidConfigurationException">The path is not set, or the file cannot be read or parsed.</exception>
    public SendPolicyRuleFile(ISendPolicyConfig config, IFileSystem fileSystem, ILogManager logManager)
    {
        if (string.IsNullOrWhiteSpace(config.RulesPath))
            throw new InvalidConfigurationException($"{nameof(ISendPolicyConfig.RulesPath)} must be set when SendPolicy is enabled.", ExitCodes.ConflictingConfigurations);
        _path = config.RulesPath;
        _fileSystem = fileSystem;
        _logger = logManager.GetClassLogger<SendPolicyRuleFile>();
        if (Current.Error is { } error)
            throw new InvalidConfigurationException($"SendPolicy rules at {_path}: {error}", ExitCodes.ConflictingConfigurations);
    }

    public SendPolicyRules Current
    {
        get
        {
            Loaded? loaded = _loaded;
            FileSnapshot snapshot = FileSnapshot.Of(_path, _fileSystem, loaded?.Snapshot.Contents);
            if (loaded is not null && loaded.Snapshot == snapshot) return loaded.Rules;
            lock (_reloadLock)
            {
                // A delayed reader must not replace a newer cached snapshot.
                loaded = _loaded;
                snapshot = FileSnapshot.Of(_path, _fileSystem, loaded?.Snapshot.Contents);
                if (loaded is not null && loaded.Snapshot == snapshot) return loaded.Rules;
                SendPolicyRules rules = Read(snapshot, loaded?.LastGood ?? SendPolicyRules.Empty);
                _loaded = new Loaded(snapshot, rules, rules.Error is null ? rules : loaded?.LastGood ?? SendPolicyRules.Empty);
                return rules;
            }
        }
    }

    private SendPolicyRules Read(FileSnapshot snapshot, SendPolicyRules lastGood)
    {
        SendPolicyRules rules;
        try
        {
            rules = snapshot.Error is { } error
                ? SendPolicyRules.Unusable(lastGood, $"rule file unusable ({error})")
                : SendPolicyRules.Parse(ReadLines(snapshot.Contents!));
        }
        catch (FormatException e)
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

    private static IEnumerable<string> ReadLines(string contents)
    {
        using StringReader reader = new(contents);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private sealed record Loaded(FileSnapshot Snapshot, SendPolicyRules Rules, SendPolicyRules LastGood);

    // File metadata alone misses atomic replacements that preserve their size and last-write time.
    // Compare the decoded content, and represent read errors as snapshots so cached rules fail closed.
    private readonly record struct FileSnapshot(string? Contents, string? Error)
    {
        public static FileSnapshot Of(string path, IFileSystem fileSystem, string? previousContents)
        {
            try
            {
                using Stream stream = fileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader reader = new(stream);
                return new FileSnapshot(ReadContents(reader, previousContents), null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new FileSnapshot(null, e.Message);
            }
        }

        private static string ReadContents(StreamReader reader, string? previousContents)
        {
            if (previousContents is null) return reader.ReadToEnd();

            Span<char> buffer = stackalloc char[1024];
            int position = 0;
            int count;
            while ((count = reader.Read(buffer)) != 0)
            {
                ReadOnlySpan<char> chunk = buffer[..count];
                if (count > previousContents.Length - position ||
                    !chunk.SequenceEqual(previousContents.AsSpan(position, count)))
                {
                    StringBuilder contents = new();
                    contents.Append(previousContents.AsSpan(0, position));
                    contents.Append(chunk);
                    contents.Append(reader.ReadToEnd());
                    return contents.ToString();
                }

                position += count;
            }

            return position == previousContents.Length ? previousContents : previousContents[..position];
        }
    }
}
