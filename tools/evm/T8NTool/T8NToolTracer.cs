using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Evm.T8NTool
{
    public class T8NToolTracer : IBlockTracer, ITxTracer, IJournal<int>, ITxTracerWrapper
    {
        private ITxTracer _currentTxTracer = NullTxTracer.Instance;
        public ITxTracer InnerTracer => _currentTxTracer;
        private readonly BlockReceiptsTracer _receiptTracer = new();
        public TxReceipt LastReceipt => _receiptTracer.LastReceipt;
        public IReadOnlyList<TxReceipt> TxReceipts => _receiptTracer.TxReceipts;
        public Dictionary<Address, Dictionary<UInt256, byte[]>> storages = new();

        public void ReportReward(Address author, string rewardType, UInt256 rewardValue)
        {
            _receiptTracer.ReportReward(author, rewardType, rewardValue);
        }

        public void StartNewBlockTrace(Block block)
        {
            _receiptTracer.StartNewBlockTrace(block);
        }

        public ITxTracer StartNewTxTrace(Transaction? tx)
        {
            return _receiptTracer.StartNewTxTrace(tx);
        }

        public void EndTxTrace()
        {
            _receiptTracer.EndTxTrace();
        }

        public void EndBlockTrace()
        {
            _receiptTracer.EndBlockTrace();
        }

        public bool IsTracingRewards => _receiptTracer.IsTracingRewards;

        public void ReportBalanceChange(Address address, UInt256? before, UInt256? after)
        {
            _receiptTracer.ReportBalanceChange(address, before, after);
        }

        public void ReportCodeChange(Address address, byte[]? before, byte[]? after)
        {
            _receiptTracer.ReportCodeChange(address, before, after);
        }

        public void ReportNonceChange(Address address, UInt256? before, UInt256? after)
        {
            _receiptTracer.ReportNonceChange(address, before, after);
        }

        public void ReportAccountRead(Address address)
        {
            _receiptTracer.ReportAccountRead(address);
        }

        public bool IsTracingState => _receiptTracer.IsTracingState;

        public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
        {
            _receiptTracer.ReportStorageChange(key, value);
        }

        public void ReportStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
        {
            _receiptTracer.ReportStorageChange(storageCell, before, after);
        }

        public void ReportStorageRead(in StorageCell storageCell)
        {
            _receiptTracer.ReportStorageRead(storageCell);
        }

        public bool IsTracingStorage => _receiptTracer.IsTracingStorage;

        public void Dispose()
        {
            _receiptTracer.Dispose();
        }

        public void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
        {
            _receiptTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);
        }

        public void MarkAsFailed(Address recipient, long gasSpent, byte[] output, string error, Hash256? stateRoot = null)
        {
            _receiptTracer.MarkAsFailed(recipient, gasSpent, output, error, stateRoot);
        }

        public void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge = false)
        {
            _receiptTracer.StartOperation(depth, gas, opcode, pc, isPostMerge);
        }

        public void ReportOperationError(EvmExceptionType error)
        {
            _receiptTracer.ReportOperationError(error);
        }

        public void ReportOperationRemainingGas(long gas)
        {
            _receiptTracer.ReportOperationRemainingGas(gas);
        }

        public void SetOperationStack(TraceStack stack)
        {
            _receiptTracer.SetOperationStack(stack);
        }

        public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
        {
            _receiptTracer.ReportStackPush(stackItem);
        }

        public void SetOperationMemory(TraceMemory memoryTrace)
        {
            _receiptTracer.SetOperationMemory(memoryTrace);
        }

        public void SetOperationMemorySize(ulong newSize)
        {
            _receiptTracer.SetOperationMemorySize(newSize);
        }

        public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
        {
            _receiptTracer.ReportMemoryChange(offset, data);
        }

        public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
        {
            if (!storages.TryGetValue(address, out _))
            {
                storages[address] = [];
            }
            storages[address][storageIndex] = newValue.ToArray();
        }

        public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) { }

        public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress)
        {
            _receiptTracer.ReportSelfDestruct(address, balance, refundAddress);
        }

        public void ReportAction(long gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
        {
            _receiptTracer.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        }

        public void ReportActionEnd(long gas, ReadOnlyMemory<byte> output)
        {
            _receiptTracer.ReportActionEnd(gas, output);
        }

        public void ReportActionError(EvmExceptionType evmExceptionType)
        {
            _receiptTracer.ReportActionError(evmExceptionType);
        }

        public void ReportActionEnd(long gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
        {
            _receiptTracer.ReportActionEnd(gas, deploymentAddress, deployedCode);
        }

        public void ReportBlockHash(Hash256 blockHash)
        {
            _receiptTracer.ReportBlockHash(blockHash);
        }

        public void ReportByteCode(ReadOnlyMemory<byte> byteCode)
        {
            _receiptTracer.ReportByteCode(byteCode);
        }

        public void ReportGasUpdateForVmTrace(long refund, long gasAvailable)
        {
            _receiptTracer.ReportGasUpdateForVmTrace(refund, gasAvailable);
        }

        public void ReportRefund(long refund)
        {
            _receiptTracer.ReportRefund(refund);
        }

        public void ReportExtraGasPressure(long extraGasPressure)
        {
            _receiptTracer.ReportExtraGasPressure(extraGasPressure);
        }

        public void ReportAccess(IReadOnlySet<Address> accessedAddresses, IReadOnlySet<StorageCell> accessedStorageCells)
        {
            _receiptTracer.ReportAccess(accessedAddresses, accessedStorageCells);
        }

        public void ReportFees(UInt256 fees, UInt256 burntFees)
        {
            _receiptTracer.ReportFees(fees, burntFees);
        }

        public bool IsTracingReceipt => _receiptTracer.IsTracingReceipt;

        public bool IsTracingActions => _receiptTracer.IsTracingActions;

        public bool IsTracingOpLevelStorage => true;

        public bool IsTracingMemory => _receiptTracer.IsTracingMemory;

        public bool IsTracingInstructions => _receiptTracer.IsTracingInstructions;

        public bool IsTracingRefunds => _receiptTracer.IsTracingRefunds;

        public bool IsTracingCode => _receiptTracer.IsTracingCode;

        public bool IsTracingStack => _receiptTracer.IsTracingStack;

        public bool IsTracingBlockHash => _receiptTracer.IsTracingBlockHash;

        public bool IsTracingAccess => _receiptTracer.IsTracingAccess;

        public bool IsTracingFees => _receiptTracer.IsTracingFees;

        public int TakeSnapshot()
        {
            return _receiptTracer.TakeSnapshot();
        }

        public void Restore(int snapshot)
        {
            _receiptTracer.Restore(snapshot);
        }
    }
}
