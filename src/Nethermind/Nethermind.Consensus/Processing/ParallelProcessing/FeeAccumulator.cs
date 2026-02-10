// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

/// <summary>
/// Accumulates transaction fees to avoid false dependencies between all transactions
/// when they all write to the same GasBeneficiary/FeeCollector accounts.
/// </summary>
/// <remarks>
/// In Block-STM, every transaction pays fees to GasBeneficiary and optionally FeeCollector.
/// Without fee accumulation, all transactions would have write-after-write dependencies on these accounts.
/// This class tracks fee deltas separately and allows them to be applied atomically after parallel execution.
/// </remarks>
public class FeeAccumulator(int txCount, Address? gasBeneficiary, Address? feeCollector)
{
    // Per-transaction fee deltas
    private readonly UInt256[] _gasBeneficiaryFees = new UInt256[txCount];
    private readonly UInt256[] _feeCollectorFees = new UInt256[txCount];

    // Committed status per transaction
    private readonly bool[] _committed = new bool[txCount];
    private readonly bool[] _gasBeneficiaryCreates = new bool[txCount];

    /// <summary>
    /// Gets the GasBeneficiary address.
    /// </summary>
    public Address? GasBeneficiary => gasBeneficiary;

    /// <summary>
    /// Gets the FeeCollector address.
    /// </summary>
    public Address? FeeCollector => feeCollector;

    /// <summary>
    /// Indicates whether any gas beneficiary payments were recorded.
    /// </summary>
    public bool HasGasBeneficiaryPayments
    {
        get
        {
            for (int i = 0; i < _committed.Length; i++)
            {
                if (Volatile.Read(ref _committed[i]) && Volatile.Read(ref _gasBeneficiaryCreates[i]))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private bool IsFeeCollector(Address address) => AreSame(address, feeCollector);
    private bool IsGasBeneficiary(Address address) => AreSame(address, gasBeneficiary);
    private static bool AreSame(Address address, Address? feesAddress) => feesAddress is not null && address == feesAddress;

    /// <summary>
    /// Records a fee payment for a transaction.
    /// </summary>
    /// <param name="txIndex">Transaction index</param>
    /// <param name="recipient">Fee recipient address</param>
    /// <param name="amount">Fee amount</param>
    /// <param name="createAccount">True when the fee transfer should create the recipient account if missing.</param>
    public void RecordFee(int txIndex, Address recipient, in UInt256 amount, bool createAccount)
    {
        if (IsGasBeneficiary(recipient))
        {
            _gasBeneficiaryFees[txIndex] += amount;
            if (createAccount)
            {
                Volatile.Write(ref _gasBeneficiaryCreates[txIndex], true);
            }
        }
        else if (IsFeeCollector(recipient))
        {
            _feeCollectorFees[txIndex] += amount;
        }
    }

    /// <summary>
    /// Marks a transaction as committed.
    /// </summary>
    /// <param name="txIndex">Transaction index</param>
    public void MarkCommitted(int txIndex) => Interlocked.Exchange(ref _committed[txIndex], true);

    /// <summary>
    /// Returns whether the transaction fees have been committed for the given transaction.
    /// </summary>
    /// <param name="txIndex">Transaction index</param>
    public bool IsCommitted(int txIndex) => Volatile.Read(ref _committed[txIndex]);

    /// <summary>
    /// Gets the accumulated fees for a fee recipient up to (but not including) the specified transaction index.
    /// Only includes fees from committed transactions.
    /// </summary>
    /// <param name="recipient">Fee recipient address</param>
    /// <param name="upToTxIndex">Transaction index (exclusive)</param>
    /// <returns>Total accumulated fees from committed transactions</returns>
    public UInt256 GetAccumulatedFees(Address recipient, int upToTxIndex)
    {
        UInt256 total = UInt256.Zero;

        UInt256[]? fees =
            IsGasBeneficiary(recipient) ? _gasBeneficiaryFees :
            IsFeeCollector(recipient) ? _feeCollectorFees : null;

        if (fees is not null)
        {
            for (int i = 0; i < upToTxIndex && i < txCount; i++)
            {
                if (Volatile.Read(ref _committed[i]))
                {
                    total += fees[i];
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Gets the total accumulated fees for a fee recipient from all transactions.
    /// </summary>
    /// <param name="recipient">Fee recipient address</param>
    /// <returns>Total accumulated fees</returns>
    public UInt256 GetTotalFees(Address recipient) => GetAccumulatedFees(recipient, txCount);

    /// <summary>
    /// Clears the fee for a transaction. Used when a transaction is re-executed.
    /// </summary>
    /// <param name="txIndex">Transaction index</param>
    public void ClearFee(int txIndex)
    {
        _gasBeneficiaryFees[txIndex] = UInt256.Zero;
        _feeCollectorFees[txIndex] = UInt256.Zero;
        Interlocked.Exchange(ref _committed[txIndex], false);
        Volatile.Write(ref _gasBeneficiaryCreates[txIndex], false);
    }

    public FeeRecipientKind GetFeeKind(Address recipient) =>
        IsGasBeneficiary(recipient) ? FeeRecipientKind.GasBeneficiary :
        IsFeeCollector(recipient) ? FeeRecipientKind.FeeCollector :
        FeeRecipientKind.None;
}
