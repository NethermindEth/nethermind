// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Checked ZK gas accounting for a single block execution.
/// Tracks per-transaction and per-block ZK gas usage, enforcing the block limit.
/// </summary>
/// <remarks>
/// Production code should resolve the limit via
/// <see cref="ZkGasSchedule.ResolveBlockZkGasLimit(ulong)"/> using the chain id from the
/// active spec provider so Masaya gets its larger budget while every other network keeps
/// the canonical 100M cap. The default <see cref="ZkGasSchedule.BlockZkGasLimit"/> is used
/// when the meter is constructed without an explicit limit (tests and network-agnostic
/// call sites).
/// </remarks>
/// <param name="blockZkGasLimit">Maximum ZK gas permitted within a single block.</param>
public class ZkGasMeter(ulong blockZkGasLimit = ZkGasSchedule.BlockZkGasLimit)
{
    /// <summary>Per-block ZK gas ceiling captured at construction time.</summary>
    private readonly ulong _blockZkGasLimit = blockZkGasLimit;

    /// <summary>Finalized ZK gas accumulated from fully committed transactions.</summary>
    private ulong _blockZkGasUsed;

    /// <summary>In-flight ZK gas accumulated for the currently executing transaction.</summary>
    private ulong _txZkGasUsed;

    /// <summary>The block ZK gas ceiling enforced by this meter.</summary>
    public ulong BlockZkGasLimit => _blockZkGasLimit;

    /// <summary>Whether the block ZK gas limit has been exceeded.</summary>
    public bool IsLimitExceeded { get; private set; }

    /// <summary>Returns the finalized ZK gas from fully committed transactions.</summary>
    public ulong BlockZkGasUsed => _blockZkGasUsed;

    /// <summary>Returns the in-flight ZK gas for the current transaction.</summary>
    public ulong TxZkGasUsed => _txZkGasUsed;

    /// <summary>Resets the in-flight ZK gas for the current transaction.</summary>
    public void ResetTransaction() => _txZkGasUsed = 0;

    /// <summary>
    /// Clears all per-block accounting so the meter can be reused for the next block
    /// without allocating a fresh instance. The block ZK gas limit is preserved.
    /// </summary>
    public void ResetBlock()
    {
        _blockZkGasUsed = 0;
        _txZkGasUsed = 0;
        IsLimitExceeded = false;
    }

    /// <summary>
    /// Promotes the current transaction's ZK gas into the finalized block total.
    /// Returns false if the commit would exceed the block limit.
    /// When the commit succeeds, <see cref="IsLimitExceeded"/> is reset to <c>false</c>
    /// so that a temporary projection spike during opcode charging does not bleed into
    /// subsequent transactions.
    /// </summary>
    public bool CommitTransaction()
    {
        ulong next = _blockZkGasUsed + _txZkGasUsed;
        if (next < _blockZkGasUsed) // overflow
        {
            IsLimitExceeded = true;
            _txZkGasUsed = 0;
            return false;
        }

        if (next > _blockZkGasLimit)
        {
            IsLimitExceeded = true;
            _txZkGasUsed = 0;
            return false;
        }

        _blockZkGasUsed = next;
        _txZkGasUsed = 0;
        IsLimitExceeded = false; // successful commit – clear any temporary projection spike
        return true;
    }

    /// <summary>
    /// Cancels the in-flight transaction, discarding its accumulated ZK gas and clearing
    /// <see cref="IsLimitExceeded"/>. Call this when a transaction is rolled back so that
    /// the spike that triggered exclusion does not block subsequent transactions.
    /// </summary>
    public void CancelTransaction()
    {
        _txZkGasUsed = 0;
        IsLimitExceeded = false;
    }

    /// <summary>
    /// Charges ZK gas for a single opcode execution.
    /// Returns false if the charge would exceed the block limit.
    /// </summary>
    public bool ChargeOpcode(byte opcode, ulong rawGas)
    {
        ulong multiplier = ZkGasSchedule.OpcodeMultipliers[opcode];
        return ChargeAmount(rawGas, multiplier);
    }

    /// <summary>
    /// Charges ZK gas for a single precompile execution.
    /// Returns false if the charge would exceed the block limit.
    /// </summary>
    public bool ChargePrecompile(byte addressLowByte, ulong gasUsed)
    {
        ulong multiplier = ZkGasSchedule.PrecompileMultipliers[addressLowByte];
        return ChargeAmount(gasUsed, multiplier);
    }

    /// <summary>
    /// Charges <paramref name="rawGas"/> * <paramref name="multiplier"/> ZK gas against the
    /// current transaction budget. Returns <c>true</c> on success; returns <c>false</c> and sets
    /// <see cref="IsLimitExceeded"/> when any overflow or limit check fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Invariant – do not call <see cref="CommitTransaction"/> while
    /// <see cref="IsLimitExceeded"/> is <c>true</c>.</strong>
    /// When this method returns <c>false</c>, <c>_txZkGasUsed</c> is intentionally <em>not</em>
    /// updated. The opcode has already executed inside the EVM (the tracer charges after
    /// execution), so its ZK cost was consumed but is not reflected in the tracked total.
    /// At that point <c>_txZkGasUsed</c> understates the true ZK cost of the transaction.
    /// Committing an under-counted transaction would produce a block that exceeds the ZK
    /// prover budget and cannot be proven. The caller must cancel the transaction instead
    /// (via <see cref="CancelTransaction"/>).
    /// </para>
    /// </remarks>
    private bool ChargeAmount(ulong rawGas, ulong multiplier)
    {
        // Compute charge = rawGas * multiplier with overflow check
        if (multiplier > 0 && rawGas > ulong.MaxValue / multiplier)
        {
            IsLimitExceeded = true;
            return false;
        }

        ulong charge = rawGas * multiplier;

        // Add to in-flight tx gas with overflow check
        ulong nextTx = _txZkGasUsed + charge;
        if (nextTx < _txZkGasUsed) // overflow
        {
            IsLimitExceeded = true;
            return false;
        }

        // Check projected block total
        ulong projectedBlock = _blockZkGasUsed + nextTx;
        if (projectedBlock < _blockZkGasUsed || projectedBlock > _blockZkGasLimit)
        {
            IsLimitExceeded = true;
            return false;
        }

        _txZkGasUsed = nextTx;
        return true;
    }
}
