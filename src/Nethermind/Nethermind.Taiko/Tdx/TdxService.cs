// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Service for TDX signing operations.
/// </summary>
public class TdxService : ITdxService
{

    private readonly ISurgeTdxConfig _config;
    private readonly ITdxsClient _client;
    private readonly ILogger _logger;
    private readonly Ecdsa _ecdsa = new();

    private TdxGuestInfo? _guestInfo;
    private PrivateKey? _privateKey;

    public TdxService(
        ISurgeTdxConfig config,
        ITdxsClient client,
        ILogManager logManager)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("TDX attestation is only supported on Linux");
        }

        _config = config;
        _client = client;
        _logger = logManager.GetClassLogger();

        TryLoadBootstrap();
    }

    public bool IsBootstrapped => _guestInfo is not null && _privateKey is not null;

    public TdxGuestInfo Bootstrap()
    {
        if (IsBootstrapped)
        {
            _logger.Info("Already bootstrapped, returning existing data");
            return _guestInfo!;
        }

        _logger.Info("Bootstrapping TDX service");

        // Generate private key
        using var keyGenerator = new PrivateKeyGenerator();
        _privateKey = keyGenerator.Generate();
        Address address = _privateKey.Address;

        _logger.Info($"Generated TDX instance address: {address}");

        // Get TDX quote with address as user data (padded to 32 bytes)
        byte[] userData = new byte[32];
        address.Bytes.CopyTo(userData.AsSpan(12));

        byte[] nonce = new byte[32];
        new CryptoRandom().GenerateRandomBytes(nonce);

        byte[] quote = _client.Issue(userData, nonce);
        TdxMetadata metadata = _client.GetMetadata();

        _guestInfo = new TdxGuestInfo
        {
            IssuerType = metadata.IssuerType,
            PublicKey = address.ToString(),
            Quote = Convert.ToHexString(quote).ToLowerInvariant(),
            Nonce = Convert.ToHexString(nonce).ToLowerInvariant(),
            Metadata = metadata.Metadata
        };

        SaveBootstrap(_guestInfo);
        _logger.Info($"TDX bootstrap complete. Quote length: {quote.Length} bytes");

        return _guestInfo;
    }

    public TdxGuestInfo? GetGuestInfo()
    {
        return _guestInfo;
    }

    public TdxBlockHeaderSignature SignBlockHeader(BlockHeader blockHeader)
    {
        if (!IsBootstrapped)
            throw new TdxException("TDX service not bootstrapped");

        blockHeader.Hash ??= blockHeader.CalculateHash();

        if (blockHeader.StateRoot is null)
            throw new TdxException("Block header state root is null");

        // Concatenate blockHash and stateRoot, then hash for signing
        byte[] dataToHash = new byte[64];
        blockHeader.Hash.Bytes.CopyTo(dataToHash.AsSpan(0, 32));
        blockHeader.StateRoot.Bytes.CopyTo(dataToHash.AsSpan(32, 32));
        Hash256 signedHash = Keccak.Compute(dataToHash);

        Signature signature = _ecdsa.Sign(_privateKey!, signedHash);

        return new TdxBlockHeaderSignature
        {
            Signature = GetSignatureBytes(signature),
            BlockHash = blockHeader.Hash,
            StateRoot = blockHeader.StateRoot,
            Header = new BlockHeaderForRpc(blockHeader)
        };
    }

    /// <summary>
    /// Get 65-byte signature in format [r:32][s:32][v:1] where v = 27 + recovery_id
    /// </summary>
    private static byte[] GetSignatureBytes(Signature signature)
    {
        byte[] result = new byte[Signature.Size];
        signature.Bytes.CopyTo(result.AsSpan(0, Signature.Size - 1)); // r + s
        result[Signature.Size - 1] = (byte)signature.V;
        return result;
    }

    private bool TryLoadBootstrap()
    {
        string path = GetBootstrapPath();
        string keyPath = GetKeyPath();

        if (!File.Exists(path) || !File.Exists(keyPath))
            return false;

        try
        {
            string json = File.ReadAllText(path);
            _guestInfo = JsonSerializer.Deserialize<TdxGuestInfo>(json);

            byte[] keyBytes = File.ReadAllBytes(keyPath);
            _privateKey = new PrivateKey(keyBytes);

            if (IsBootstrapped)
            {
                _logger.Info($"Loaded TDX bootstrap data. Address: {_privateKey.Address}");
                return true;
            }

            _logger.Warn("Failed to load TDX bootstrap data, invalid data");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load bootstrap data: {ex.Message}");
            _guestInfo = null;
            _privateKey = null;
        }

        return false;
    }

    private void SaveBootstrap(TdxGuestInfo data)
    {
        string dir = GetConfigDir();
        string secretsDir = Path.Combine(dir, "secrets");
        Directory.CreateDirectory(secretsDir);

        // Save bootstrap data
        string path = GetBootstrapPath();
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

        // Save key with 0600 file permissions
        string keyPath = GetKeyPath();
        if (OperatingSystem.IsLinux())
        {
            using var fs = new FileStream(keyPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite // 0600
            });
            fs.Write(_privateKey!.KeyBytes);
        }
        else
        {
            throw new PlatformNotSupportedException("TDX attestation is only supported on Linux");
        }

        _logger.Debug($"Saved TDX key to {keyPath}");
    }

    private string GetConfigDir()
    {
        string configPath = _config.ConfigPath;
        if (configPath.StartsWith("~/"))
        {
            configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                configPath[2..]);
        }
        return configPath;
    }

    private string GetBootstrapPath() => Path.Combine(GetConfigDir(), "bootstrap.json");
    private string GetKeyPath() => Path.Combine(GetConfigDir(), "secrets", "priv.key");
}

/// <summary>
/// Null implementation of ITdxService for when TDX is disabled.
/// </summary>
internal sealed class NullTdxService : ITdxService
{
    public static readonly NullTdxService Instance = new();
    private NullTdxService() { }

    public bool IsBootstrapped => false;
    public TdxGuestInfo? GetGuestInfo() => null;
    public TdxGuestInfo Bootstrap() => throw new TdxException("TDX is not enabled");
    public TdxBlockHeaderSignature SignBlockHeader(BlockHeader blockHeader) => throw new TdxException("TDX is not enabled");
}
