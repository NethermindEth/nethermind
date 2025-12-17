// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.Taiko.Tdx;

/// <summary>
/// Service for generating TDX attestations.
/// </summary>
public class TdxService : ITdxService
{
    // Proof layout: [instance_id: 4 bytes][address: 20 bytes][signature: 65 bytes]
    private const int ProofSize = 89;

    // ABI encoding constants
    private const int AbiSlotSize = 32;
    private const int CheckpointHashSize = 3 * AbiSlotSize; // blockNumber + blockHash + stateRoot
    private const int InstanceHashSize = 5 * AbiSlotSize;   // tag + chainId + parentHash + checkpointHash + address

    private readonly ISurgeTdxConfig _config;
    private readonly ITdxsClient _client;
    private readonly ILogger _logger;
    private readonly Ecdsa _ecdsa = new();
    private readonly ulong _chainId;

    private TdxGuestInfo? _guestInfo;
    private PrivateKey? _privateKey;

    public TdxService(
        ISurgeTdxConfig config,
        ITdxsClient client,
        ulong chainId,
        ILogManager logManager)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("TDX attestation is only supported on Linux");
        }

        _config = config;
        _client = client;
        _chainId = chainId;
        _logger = logManager.GetClassLogger();

        TryLoadBootstrap();
    }

    public bool IsAvailable => _guestInfo is not null && _privateKey is not null;

    public TdxGuestInfo Bootstrap()
    {
        if (_guestInfo is not null && _privateKey is not null)
        {
            _logger.Info("Already bootstrapped, returning existing data");
            return _guestInfo;
        }

        _logger.Info("Bootstrapping TDX service");

        // Generate private key
        using var keyGenerator = new PrivateKeyGenerator();
        _privateKey = keyGenerator.Generate();
        Address address = _privateKey.Address;

        _logger.Info($"Generated TDX instance address: {address}");

        // Get TDX quote with address as user data (padded to 32 bytes)
        byte[] userData = new byte[32];
        address.Bytes.CopyTo(userData.AsSpan(12)); // Right-pad address in 32 bytes

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

    public TdxAttestation Attest(Block block)
    {
        if (_guestInfo is null || _privateKey is null)
            throw new TdxException("TDX service not bootstrapped");

        // Compute instance hash for the block
        Hash256 instanceHash = ComputeInstanceHash(block);

        // Sign the instance hash
        Signature signature = _ecdsa.Sign(_privateKey, instanceHash.ValueHash256);
        byte[] signatureBytes = GetSignatureBytes(signature);

        // Generate TDX quote with instance hash as user data
        byte[] nonce = new byte[32];
        new CryptoRandom().GenerateRandomBytes(nonce);
        byte[] instanceHashBytes = instanceHash.Bytes.ToArray();
        byte[] quote = _client.Issue(instanceHashBytes, nonce);

        // Build proof: instance_id (4) + address (20) + signature (65)
        byte[] proof = BuildProof(_privateKey.Address, signatureBytes, _config.InstanceId);

        return new TdxAttestation
        {
            Proof = proof,
            Quote = quote,
            InstanceHash = instanceHash
        };
    }

    /// <summary>
    /// Build the 89-byte proof: [instance_id:4][address:20][signature:65]
    /// </summary>
    private static byte[] BuildProof(Address address, byte[] signature, uint instanceId)
    {
        byte[] proof = new byte[ProofSize];

        // Instance ID (4 bytes, big endian)
        proof[0] = (byte)(instanceId >> 24);
        proof[1] = (byte)(instanceId >> 16);
        proof[2] = (byte)(instanceId >> 8);
        proof[3] = (byte)instanceId;

        // Address (20 bytes)
        address.Bytes.CopyTo(proof.AsSpan(4, 20));

        // Signature (65 bytes)
        signature.AsSpan(0, 65).CopyTo(proof.AsSpan(24));

        return proof;
    }

    /// <summary>
    /// Get 65-byte signature in format [r:32][s:32][v:1] where v = 27 + recovery_id
    /// </summary>
    private static byte[] GetSignatureBytes(Signature signature)
    {
        byte[] result = new byte[65];
        signature.Bytes.CopyTo(result.AsSpan(0, 64)); // r + s
        result[64] = (byte)(signature.RecoveryId + 27); // v = recovery_id + 27
        return result;
    }

    /// <summary>
    /// Compute the instance hash for a preconf block attestation.
    /// Structure: keccak256(abi.encode("PRECONF_CHECKPOINT", chainId, parentHash, checkpointHash, proverAddress))
    /// Where checkpointHash = keccak256(abi.encode(blockNumber, blockHash, stateRoot))
    ///
    /// ABI layout (160 bytes = 5 slots × 32 bytes):
    /// ┌─────────┬──────┬───────────────────────────────────────────────────┐
    /// │ Offset  │ Slot │ Content                                           │
    /// ├─────────┼──────┼───────────────────────────────────────────────────┤
    /// │   0-31  │ 0    │ "PRECONF_CHECKPOINT" (18 bytes) + zero padding    │
    /// │  32-63  │ 1    │ chainId as uint256 (value at bytes 56-63)         │
    /// │  64-95  │ 2    │ parentBlockHash (32 bytes)                        │
    /// │  96-127 │ 3    │ checkpointHash (32 bytes)                         │
    /// │ 128-159 │ 4    │ proverAddress (12 zero bytes + 20 address bytes)  │
    /// └─────────┴──────┴───────────────────────────────────────────────────┘
    /// </summary>
    private Hash256 ComputeInstanceHash(Block block)
    {
        Hash256 checkpointHash = ComputeCheckpointHash(block);

        Span<byte> buffer = stackalloc byte[InstanceHashSize];

        "PRECONF_CHECKPOINT"u8.CopyTo(buffer);                               // Slot 0: string tag
        BinaryPrimitives.WriteUInt64BigEndian(buffer[56..], _chainId);       // Slot 1: chainId (big-endian at end)
        (block.Header.ParentHash ?? Keccak.Zero).Bytes.CopyTo(buffer[64..]); // Slot 2: parentHash
        checkpointHash.Bytes.CopyTo(buffer[96..]);                           // Slot 3: checkpointHash
        (_privateKey?.Address ?? Address.Zero).Bytes.CopyTo(buffer[140..]);  // Slot 4: address (left-padded)

        return Keccak.Compute(buffer);
    }

    /// <summary>
    /// Compute checkpoint hash: keccak256(abi.encode(blockNumber, blockHash, stateRoot))
    ///
    /// ABI layout (96 bytes = 3 slots × 32 bytes):
    /// ┌────────┬──────┬─────────────────────────────────────────────────┐
    /// │ Offset │ Slot │ Content                                         │
    /// ├────────┼──────┼─────────────────────────────────────────────────┤
    /// │  0-31  │ 0    │ blockNumber as uint256 (value at bytes 24-31)   │
    /// │ 32-63  │ 1    │ blockHash (32 bytes)                            │
    /// │ 64-95  │ 2    │ stateRoot (32 bytes)                            │
    /// └────────┴──────┴─────────────────────────────────────────────────┘
    /// </summary>
    private static Hash256 ComputeCheckpointHash(Block block)
    {
        Span<byte> buffer = stackalloc byte[CheckpointHashSize];

        BinaryPrimitives.WriteUInt64BigEndian(buffer[24..], (ulong)block.Number); // Slot 0: blockNumber
        (block.Header.Hash ?? Keccak.Zero).Bytes.CopyTo(buffer[32..]);            // Slot 1: blockHash
        (block.Header.StateRoot ?? Keccak.Zero).Bytes.CopyTo(buffer[64..]);       // Slot 2: stateRoot

        return Keccak.Compute(buffer);
    }

    private void TryLoadBootstrap()
    {
        string path = GetBootstrapPath();
        string keyPath = GetKeyPath();

        if (!File.Exists(path) || !File.Exists(keyPath))
            return;

        try
        {
            string json = File.ReadAllText(path);
            _guestInfo = JsonSerializer.Deserialize<TdxGuestInfo>(json);

            byte[] keyBytes = File.ReadAllBytes(keyPath);
            _privateKey = new PrivateKey(keyBytes);
            _logger.Info($"Loaded TDX bootstrap data. Address: {_privateKey.Address}");
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to load bootstrap data: {ex.Message}");
            _guestInfo = null;
            _privateKey = null;
        }
    }

    private void SaveBootstrap(TdxGuestInfo data)
    {
        string dir = GetConfigDir();
        string secretsDir = Path.Combine(dir, "secrets");
        Directory.CreateDirectory(secretsDir);

        // Save bootstrap data
        string path = GetBootstrapPath();
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

        // Save key with atomic 0600 permissions
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
