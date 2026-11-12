// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;

namespace Nethermind.Eez.Execution;

/// <summary>
/// RollupX's fixed header profile on top of the Ethereum header rules: an exact 30M gas limit, the block's slot on
/// the grid as its timestamp, no ommers, difficulty or nonce, an empty withdrawals root, a zero parent beacon root,
/// the hash of no requests, no blob gas, and no block access list or slot number.
/// </summary>
public sealed class RollupXHeaderValidator(IHeaderValidator inner, EezChainRules rules) : IHeaderValidator
{
    private readonly RollupXSchedule _schedule = rules.Schedule
        ?? throw new ArgumentException("RollupX header rules need a RollupX chain.", nameof(rules));

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error) =>
        ValidateProfile(header, out error) && inner.Validate(header, parent, isUncle, out error);

    public bool Validate(BlockHeader header, BlockHeader parent, bool isUncle, [NotNullWhen(false)] out string? error, bool validateHash) =>
        ValidateProfile(header, out error) && inner.Validate(header, parent, isUncle, out error, validateHash);

    public bool ValidateOrphaned(BlockHeader header, [NotNullWhen(false)] out string? error) =>
        ValidateProfile(header, out error) && inner.ValidateOrphaned(header, out error);

    private bool ValidateProfile(BlockHeader header, [NotNullWhen(false)] out string? error)
    {
        error = ProfileMismatch(header, _schedule) is { } mismatch ? $"Invalid RollupX header {header.Number}: {mismatch}." : null;
        return error is null;
    }

    /// <returns>The first way <paramref name="header"/> breaks the fixed profile, or <see langword="null"/> when it keeps it.</returns>
    public static string? ProfileMismatch(BlockHeader header, RollupXSchedule schedule) =>
        header switch
        {
            { GasLimit: not EezChainRules.RollupXBlockGasLimit } => $"gas limit must be {EezChainRules.RollupXBlockGasLimit}",
            _ when header.Timestamp != schedule.Timestamp(header.Number) => $"timestamp must be {schedule.Timestamp(header.Number)}, the grid's for block {header.Number}",
            _ when header.UnclesHash != Keccak.OfAnEmptySequenceRlp => "ommers must be empty",
            _ when !header.Difficulty.IsZero || header.Nonce != 0 => "difficulty and nonce must be zero",
            _ when header.WithdrawalsRoot != Keccak.EmptyTreeHash => "the withdrawals root must be the empty root",
            _ when header.ParentBeaconBlockRoot != Keccak.Zero => "the parent beacon root must be zero",
            _ when header.RequestsHash != ExecutionRequestExtensions.EmptyRequestsHash => "the requests hash must be the hash of no requests",
            { BlobGasUsed: not 0 } or { ExcessBlobGas: not 0 } => "blob gas used and excess blob gas must be zero",
            { BlockAccessListHash: not null } or { SlotNumber: not null } => "a block access list hash or slot number is not allowed",
            _ => null,
        };
}
