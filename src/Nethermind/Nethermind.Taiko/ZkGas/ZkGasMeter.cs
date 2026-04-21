// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.ZkGas;

/// <summary>
/// Checked ZK gas accounting for a single block execution.
/// Tracks per-transaction and per-block ZK gas usage, enforcing the block limit.
/// </summary>
public class ZkGasMeter
{
    /// <summary>Finalized ZK gas accumulated from fully committed transactions.</summary>
    private ulong _blockZkGasUsed;

    /// <summary>In-flight ZK gas accumulated for the currently executing transaction.</summary>
    private ulong _txZkGasUsed;

    /// <summary>Whether the block ZK gas limit has been exceeded.</summary>
    public bool IsLimitExceeded { get; private set; }

    /// <summary>Returns the finalized ZK gas from fully committed transactions.</summary>
    public ulong BlockZkGasUsed => _blockZkGasUsed;

    /// <summary>Returns the in-flight ZK gas for the current transaction.</summary>
    public ulong TxZkGasUsed => _txZkGasUsed;

    /// <summary>Resets the in-flight ZK gas for the current transaction.</summary>
    public void ResetTransaction() => _txZkGasUsed = 0;

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

        if (next > ZkGasSchedule.BlockZkGasLimit)
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
        if (projectedBlock < _blockZkGasUsed || projectedBlock > ZkGasSchedule.BlockZkGasLimit)
        {
            IsLimitExceeded = true;
            return false;
        }

        _txZkGasUsed = nextTx;
        return true;
    }
}
