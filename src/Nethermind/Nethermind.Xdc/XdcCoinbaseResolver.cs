// SPDX-FileCopyrightText: 2026 Anil Chinchawale
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc;

/// <summary>
/// Resolves the actual fee recipient (coinbase) for XDC blocks.
/// 
/// In XDPoS, header.Coinbase is 0x0, but transaction fees should go to the masternode owner.
/// This resolver:
/// 1. Extracts the block signer from the header's extra data (ecrecover from seal)
/// 2. Looks up validatorsState[signer].owner from the validator contract (0x88)
/// 3. Returns the owner address as the actual coinbase
/// </summary>
public class XdcCoinbaseResolver
{
    // Validator contract address (MasternodeVotingSMC)
    public static readonly Address ValidatorContractAddress = 
        new("0x0000000000000000000000000000000000000088");
    
    // Foundation wallet (used as fallback)
    public static readonly Address FoundationWallet = 
        new("0x92a289fe95a85c53b8d0d113cbaef0c1ec98ac65");
    
    // Slot positions in the validator contract
    private const ulong ValidatorsStateSlot = 1;  // validatorsState mapping slot
    
    // Extra data format for V1: [32 bytes vanity][N*20 bytes signers (checkpoint only)][65 bytes seal]
    private const int ExtraVanity = 32;
    private const int ExtraSeal = 65;  // 65 bytes signature
    
    private readonly ILogger _logger;

    public XdcCoinbaseResolver(ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
    }

    /// <summary>
    /// Resolves the actual fee recipient for a block.
    /// </summary>
    /// <param name="header">The block header</param>
    /// <param name="worldState">The world state for storage lookups</param>
    /// <returns>The actual coinbase address (masternode owner)</returns>
    public Address ResolveCoinbase(BlockHeader header, IWorldState worldState)
    {
        try
        {
            // Step 1: Extract the block signer from the header signature
            Address? signer = ExtractSigner(header);
            if (signer is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Could not extract signer for block {header.Number}, using beneficiary");
                return header.Beneficiary;
            }

            Console.WriteLine($"[XDC-COINBASE] Block {header.Number}: Extracted signer {signer}");
            if (_logger.IsDebug) _logger.Debug($"Block {header.Number}: Extracted signer {signer}");

            // Step 2: Look up the owner from validatorsState[signer].owner
            Address? owner = GetValidatorOwner(signer, worldState);
            if (owner is null || owner == Address.Zero)
            {
                // Fallback: if no owner found, the signer might be the owner directly
                // or we use the foundation wallet as fallback
                if (_logger.IsDebug) _logger.Debug($"Block {header.Number}: No owner found for signer {signer}, using signer as owner");
                return signer;
            }

            Console.WriteLine($"[XDC-COINBASE] Block {header.Number}: Resolved owner {owner} for signer {signer}");
            if (_logger.IsDebug) _logger.Debug($"Block {header.Number}: Resolved owner {owner} for signer {signer}");
            return owner;
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Error resolving coinbase for block {header.Number}: {ex.Message}");
            return header.Beneficiary;
        }
    }

    /// <summary>
    /// Extracts the block signer from the header's extra data using ecrecover.
    /// V1 format: [32 bytes vanity][N*20 bytes signers (checkpoint only)][65 bytes seal]
    /// The seal is the last 65 bytes of ExtraData.
    /// </summary>
    private Address? ExtractSigner(BlockHeader header)
    {
        byte[]? extraData = header.ExtraData;
        if (extraData is null || extraData.Length < ExtraVanity + ExtraSeal)
        {
            if (_logger.IsDebug) _logger.Debug($"ExtraData too short: {extraData?.Length ?? 0} bytes");
            return null;
        }

        // Extract signature (last 65 bytes)
        byte[] signature = new byte[ExtraSeal];
        Buffer.BlockCopy(extraData, extraData.Length - ExtraSeal, signature, 0, ExtraSeal);

        // Compute hash without the signature bytes
        // The hash is computed over the header RLP encoded WITHOUT the seal bytes
        ValueHash256 hash = ComputeSigHash(header, extraData);

        // Perform ecrecover
        return RecoverAddress(signature, hash);
    }

    /// <summary>
    /// Computes the hash that was signed for the header.
    /// This is the RLP encoding of the header with ExtraData truncated (without the last 65 bytes).
    /// </summary>
    private ValueHash256 ComputeSigHash(BlockHeader header, byte[] extraData)
    {
        // Truncate extraData to exclude the seal (last 65 bytes)
        byte[] extraDataWithoutSeal = new byte[extraData.Length - ExtraSeal];
        Buffer.BlockCopy(extraData, 0, extraDataWithoutSeal, 0, extraDataWithoutSeal.Length);

        // Create a temporary header with truncated extra data for hashing
        // We need to compute the RLP hash similar to how XDC does it
        var stream = new RlpStream(GetHeaderRlpLength(header, extraDataWithoutSeal));
        EncodeHeaderForSigHash(stream, header, extraDataWithoutSeal);
        
        return ValueKeccak.Compute(stream.Data);
    }

    /// <summary>
    /// Encodes the header for sighash computation (RLP encoding without the seal).
    /// </summary>
    private void EncodeHeaderForSigHash(RlpStream stream, BlockHeader header, byte[] extraDataWithoutSeal)
    {
        // XDC V1 sigHash uses ONLY the standard 15 Ethereum header fields (NO XDC-specific fields!)
        // See geth-xdc: consensus/XDPoS/engines/engine_v1/utils.go sigHash()
        // Fields: parentHash, unclesHash, coinbase, stateRoot, txRoot, receiptsRoot, bloom,
        //         difficulty, number, gasLimit, gasUsed, timestamp, extraData(truncated), mixHash, nonce
        //         [+ optional BaseFee]
        
        int contentLength = GetContentLength(header, extraDataWithoutSeal);
        stream.StartSequence(contentLength);
        
        stream.Encode(header.ParentHash);
        stream.Encode(header.UnclesHash);
        stream.Encode(header.Beneficiary);
        stream.Encode(header.StateRoot);
        stream.Encode(header.TxRoot);
        stream.Encode(header.ReceiptsRoot);
        stream.Encode(header.Bloom);
        stream.Encode(header.Difficulty);
        stream.Encode(header.Number);
        stream.Encode(header.GasLimit);
        stream.Encode(header.GasUsed);
        stream.Encode(header.Timestamp);
        stream.Encode(extraDataWithoutSeal);
        stream.Encode(header.MixHash);
        stream.Encode(header.Nonce, 8);  // 8 bytes for nonce
        
        // NOTE: Do NOT include Validators/Validator/Penalties - geth-xdc sigHash doesn't include them!
        
        // BaseFee only if present (XDC doesn't use it, but keep for compatibility)
        if (!header.BaseFeePerGas.IsZero)
        {
            stream.Encode(header.BaseFeePerGas);
        }
    }

    private int GetContentLength(BlockHeader header, byte[] extraDataWithoutSeal)
    {
        int length = 0;
        length += Rlp.LengthOf(header.ParentHash);
        length += Rlp.LengthOf(header.UnclesHash);
        length += Rlp.LengthOf(header.Beneficiary);
        length += Rlp.LengthOf(header.StateRoot);
        length += Rlp.LengthOf(header.TxRoot);
        length += Rlp.LengthOf(header.ReceiptsRoot);
        length += Rlp.LengthOf(header.Bloom);
        length += Rlp.LengthOf(header.Difficulty);
        length += Rlp.LengthOf(header.Number);
        length += Rlp.LengthOf(header.GasLimit);
        length += Rlp.LengthOf(header.GasUsed);
        length += Rlp.LengthOf(header.Timestamp);
        length += Rlp.LengthOf(extraDataWithoutSeal);
        length += Rlp.LengthOf(header.MixHash);
        length += Rlp.LengthOfNonce(header.Nonce);
        
        // NOTE: No XDC-specific fields in sigHash!
        
        if (!header.BaseFeePerGas.IsZero)
        {
            length += Rlp.LengthOf(header.BaseFeePerGas);
        }
        
        return length;
    }

    private int GetHeaderRlpLength(BlockHeader header, byte[] extraDataWithoutSeal)
    {
        return Rlp.LengthOfSequence(GetContentLength(header, extraDataWithoutSeal));
    }

    /// <summary>
    /// Recovers the address from a signature using ecrecover.
    /// Signature format: [r (32 bytes), s (32 bytes), v (1 byte)]
    /// </summary>
    private Address? RecoverAddress(byte[] signature, ValueHash256 hash)
    {
        if (signature.Length != ExtraSeal)
        {
            if (_logger.IsDebug) _logger.Debug($"Invalid signature length: {signature.Length}");
            return null;
        }

        // Extract r, s, v from signature
        // Signature format: r (32 bytes) || s (32 bytes) || v (1 byte)
        Span<byte> r = signature.AsSpan(0, 32);
        Span<byte> s = signature.AsSpan(32, 32);
        byte v = signature[64];

        // Adjust recovery ID
        // Ethereum signatures use v = 27 or 28, we need 0 or 1 for recovery
        byte recoveryId = v >= 27 ? (byte)(v - 27) : v;

        // Combine r and s into 64-byte signature
        Span<byte> sigBytes64 = stackalloc byte[64];
        r.CopyTo(sigBytes64.Slice(0, 32));
        s.CopyTo(sigBytes64.Slice(32, 32));

        // Recover public key
        Span<byte> publicKey = stackalloc byte[65];
        bool success = SpanSecP256k1.RecoverKeyFromCompact(
            publicKey,
            hash.Bytes,
            sigBytes64,
            recoveryId,
            false);

        if (!success)
        {
            if (_logger.IsDebug) _logger.Debug("Failed to recover public key from signature");
            return null;
        }

        // Compute address from public key (skip 0x04 prefix, take next 64 bytes)
        return PublicKey.ComputeAddress(publicKey.Slice(1, 64));
    }

    /// <summary>
    /// Looks up validatorsState[signer].owner from the validator contract storage.
    /// 
    /// Solidity mapping storage layout:
    /// - validatorsState is at slot 1
    /// - For a mapping(address => ValidatorState), the slot for validatorsState[signer] is:
    ///   keccak256(abi.encode(signer, uint256(1)))
    /// - The owner field is the first field (offset 0) of the struct
    /// </summary>
    private Address? GetValidatorOwner(Address signer, IWorldState worldState)
    {
        try
        {
            // Compute the storage slot for validatorsState[signer].owner
            // locValidatorsState = keccak256(signer || slot)
            // locCandidateOwner = locValidatorsState + 0 (owner is first field)
            
            UInt256 locValidatorsState = GetLocMappingAtKey(signer, ValidatorsStateSlot);
            UInt256 locCandidateOwner = locValidatorsState;  // + 0 for .owner field

            // Read storage at contract 0x88
            var storageCell = new StorageCell(ValidatorContractAddress, new Hash256(locCandidateOwner.ToBigEndian()));
            ReadOnlySpan<byte> value = worldState.Get(storageCell);

            if (value.Length == 0)
            {
                if (_logger.IsDebug) _logger.Debug($"No owner found for signer {signer} at slot {locCandidateOwner}");
                return null;
            }

            // Convert to address (last 20 bytes of the 32-byte value)
            if (value.Length >= 20)
            {
                // The address is stored in the last 20 bytes of the 32-byte word
                ReadOnlySpan<byte> addressBytes = value.Length >= 32 
                    ? value.Slice(12, 20)  // Skip first 12 bytes (padding)
                    : value.Slice(value.Length - 20, 20);
                
                return new Address(addressBytes.ToArray());
            }

            return null;
        }
        catch (Exception ex)
        {
            if (_logger.IsDebug) _logger.Debug($"Error looking up validator owner: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Computes the storage slot for a mapping key.
    /// For mapping(key => value) at slot s, the storage location is:
    /// keccak256(abi.encode(key, s))
    /// </summary>
    private UInt256 GetLocMappingAtKey(Address key, ulong slot)
    {
        // Create the input for keccak256: key (32 bytes, left-padded) || slot (32 bytes)
        Span<byte> input = stackalloc byte[64];
        
        // Key is 20 bytes, left-pad with zeros to 32 bytes
        input.Slice(0, 12).Clear();  // First 12 bytes = 0
        key.Bytes.CopyTo(input.Slice(12, 20));  // Next 20 bytes = address
        
        // Slot is uint64, encode as 32-byte big-endian
        input.Slice(32, 24).Clear();  // First 24 bytes = 0
        BinaryPrimitives.WriteUInt64BigEndian(input.Slice(56, 8), slot);

        // Compute keccak256
        ValueHash256 hash = ValueKeccak.Compute(input);
        
        // Convert hash bytes to UInt256
        return new UInt256(hash.Bytes, isBigEndian: true);
    }
}
