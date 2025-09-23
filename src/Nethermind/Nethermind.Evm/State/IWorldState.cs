// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing.State;
using Nethermind.Int256;

namespace Nethermind.Evm.State;

public static class Out
{
    public static void Log(string log)
    {
        string targetBlock = Environment.GetEnvironmentVariable("TARGET_BLOCK_NUMBER");
        if (targetBlock == null)
            return;

        string currentBlock = Environment.GetEnvironmentVariable("CURRENT_BLOCK_NUMBER") ?? "0";
        if (currentBlock != targetBlock)
            return;

        string transactionIndex = Environment.GetEnvironmentVariable("TRANSACTION_INDEX") ?? "";

        if (Environment.GetEnvironmentVariable("DEBUG_DETAILS") == "verbose")
            Console.WriteLine($"b={currentBlock}, t={transactionIndex}, {GetCallStackString()}");

        Console.WriteLine($"b={currentBlock}, t={transactionIndex} {log}");
    }

    public static void Log(string scope, string key, string value)
    {
        string targetBlock = Environment.GetEnvironmentVariable("TARGET_BLOCK_NUMBER");
        if (targetBlock == null)
            return;

        string currentBlock = Environment.GetEnvironmentVariable("CURRENT_BLOCK_NUMBER") ?? "0";
        if (currentBlock != targetBlock)
            return;

        string transactionIndex = Environment.GetEnvironmentVariable("TRANSACTION_INDEX") ?? "";

        if (Environment.GetEnvironmentVariable("DEBUG_DETAILS") == "verbose")
            Console.WriteLine($"b={currentBlock}, t={transactionIndex}, {GetCallStackString()}");

        Console.WriteLine($"b={currentBlock}, t={transactionIndex}, s={scope}: {key} {value}");
    }

    private static List<StackFrame> GetCallStack(int skipFrames = 2, int maxDepth = 20)
    {
        var stackTrace = new StackTrace(skipFrames, fNeedFileInfo: true);
        var frames = new List<StackFrame>();

        var frameCount = Math.Min(stackTrace.FrameCount, maxDepth);

        for (int i = 0; i < frameCount; i++)
        {
            var frame = stackTrace.GetFrame(i);
            if (frame == null) continue;

            var method = frame.GetMethod();
            var fileName = frame.GetFileName() ?? string.Empty;

            // Extract just the filename without full path
            if (!string.IsNullOrEmpty(fileName))
                fileName = Path.GetFileName(fileName);

            var structureName = string.Empty;
            var methodName = method?.Name ?? "Unknown";

            if (method?.DeclaringType != null)
            {
                // Check if it's a method on a class/struct (not a static class method)
                var declaringType = method.DeclaringType;

                // Use the type name without namespace for compactness
                structureName = declaringType.IsNested
                    ? $"{declaringType.DeclaringType?.Name}.{declaringType.Name}"
                    : declaringType.Name;

                // Clean up compiler-generated names for async methods, lambdas, etc.
                if (methodName.Contains("<") && methodName.Contains(">"))
                {
                    // Extract the actual method name from compiler-generated names like <MethodName>b__0
                    var startIdx = methodName.IndexOf('<') + 1;
                    var endIdx = methodName.IndexOf('>');
                    if (startIdx < endIdx)
                        methodName = methodName.Substring(startIdx, endIdx - startIdx);
                }

                // Remove generic type parameters for readability
                if (structureName.Contains('`'))
                    structureName = structureName.Substring(0, structureName.IndexOf('`'));
            }

            frames.Add(new StackFrame
            {
                File = fileName,
                LineNumber = frame.GetFileLineNumber(),
                StructureName = structureName,
                MethodName = methodName
            });
        }

        // Reverse to show in chronological order (oldest to newest)
        frames.Reverse();

        return frames;
    }

    private static string GetCallStackString(int skipFrames = 3)
    {
        var frames = GetCallStack(skipFrames);
        if (frames.Count == 0) return string.Empty;

        var sb = new StringBuilder(frames.Count * 30);

        for (int i = 0; i < frames.Count; i++)
        {
            if (i > 0)
                sb.Append(" → ");

            var frame = frames[i];

            // Format: file:line [ClassName.]MethodName
            if (!string.IsNullOrEmpty(frame.File))
            {
                sb.Append(frame.File);
                sb.Append(':');
                sb.Append(frame.LineNumber);
                sb.Append(' ');
            }

            if (!string.IsNullOrEmpty(frame.StructureName))
            {
                sb.Append(frame.StructureName);
                sb.Append('.');
            }
            sb.Append(frame.MethodName);
        }

        return sb.ToString();
    }

    private record StackFrame
    {
        public string File { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string StructureName { get; init; } = string.Empty;
        public string MethodName { get; init; } = string.Empty;
    }
}

/// <summary>
/// Represents state that can be anchored at specific state root, snapshot, committed, reverted.
/// <see cref="BeginScope"/> must be called before any other operation, or it will throw. The returned <see cref="IDisposable"/>
/// must be disposed to close the <see cref="IWorldState"/>. Multiple block can be executed or saved within the same scope.
/// </summary>
public interface IWorldState : IJournal<Snapshot>, IReadOnlyStateProvider
{
    // For scope to create genesis.
    const BlockHeader? PreGenesis = null;

    IDisposable BeginScope(BlockHeader? baseBlock);
    bool IsInScope { get; }
    new ref readonly UInt256 GetBalance(Address address);
    new ref readonly ValueHash256 GetCodeHash(Address address);
    bool HasStateForBlock(BlockHeader? baseBlock);

    /// <summary>
    /// Return the original persistent storage value from the storage cell
    /// </summary>
    /// <param name="storageCell"></param>
    /// <returns></returns>
    byte[] GetOriginal(in StorageCell storageCell);

    /// <summary>
    /// Get the persistent storage value at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at cell</returns>
    ReadOnlySpan<byte> Get(in StorageCell storageCell);

    /// <summary>
    /// Set the provided value to persistent storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void Set(in StorageCell storageCell, byte[] newValue);

    /// <summary>
    /// Get the transient storage value at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <returns>Value at cell</returns>
    ReadOnlySpan<byte> GetTransientState(in StorageCell storageCell);

    /// <summary>
    /// Set the provided value to transient storage at the specified storage cell
    /// </summary>
    /// <param name="storageCell">Storage location</param>
    /// <param name="newValue">Value to store</param>
    void SetTransientState(in StorageCell storageCell, byte[] newValue);

    /// <summary>
    /// Reset all storage
    /// </summary>
    void Reset(bool resetBlockChanges = true);

    /// <summary>
    /// Creates a restartable snapshot.
    /// </summary>
    /// <param name="newTransactionStart"> Indicates new transaction will start here.</param>
    /// <returns>Snapshot index</returns>
    /// <remarks>
    /// If <see cref="newTransactionStart"/> is true and there are already changes in <see cref="IStorageProvider"/> then next call to
    /// <see cref="GetOriginal"/> will use changes before this snapshot as original values for this new transaction.
    /// </remarks>
    Snapshot TakeSnapshot(bool newTransactionStart = false);

    Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();

    void WarmUp(AccessList? accessList);

    void WarmUp(Address address);

    /// <summary>
    /// Clear all storage at specified address
    /// </summary>
    /// <param name="address">Contract address</param>
    void ClearStorage(Address address);

    void RecalculateStateRoot();

    void DeleteAccount(Address address);

    void CreateAccount(Address address, in UInt256 balance, in UInt256 nonce = default);
    void CreateAccountIfNotExists(Address address, in UInt256 balance, in UInt256 nonce = default);
    void CreateEmptyAccountIfDeleted(Address address);

    /// <summary>
    /// Inserts the given smart contract code into the system at the specified address,
    /// associating it with a unique code hash.
    /// </summary>
    /// <param name="address">The target address where the code is to be inserted.</param>
    /// <param name="codeHash">The hash representing the code content, used for deduplication and reference.</param>
    /// <param name="code">The bytecode to be inserted.</param>
    /// <param name="spec">The current release specification which may affect validation or processing rules.</param>
    /// <param name="isGenesis">Indicates whether the insertion is part of the genesis block setup.</param>
    /// <returns>True if the code was inserted to the database at that hash; otherwise false if it was already there.
    /// Note: This is different from whether the account has its hash updated</returns>
    bool InsertCode(Address address, in ValueHash256 codeHash, ReadOnlyMemory<byte> code, IReleaseSpec spec, bool isGenesis = false);

    void AddToBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    bool AddToBalanceAndCreateIfNotExists(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    void SubtractFromBalance(Address address, in UInt256 balanceChange, IReleaseSpec spec);

    void IncrementNonce(Address address, UInt256 delta);

    void DecrementNonce(Address address, UInt256 delta);

    void IncrementNonce(Address address) => IncrementNonce(address, UInt256.One);

    void DecrementNonce(Address address) => DecrementNonce(address, UInt256.One);

    void SetNonce(Address address, in UInt256 nonce);

    /* snapshots */

    void Commit(IReleaseSpec releaseSpec, bool isGenesis = false, bool commitRoots = true);

    void Commit(IReleaseSpec releaseSpec, IWorldStateTracer tracer, bool isGenesis = false, bool commitRoots = true);

    /// <summary>
    /// Persist the underlying changes to the storage at the specified block number. This also recalculate state root.
    /// </summary>
    /// <param name="blockNumber"></param>
    void CommitTree(long blockNumber);

    ArrayPoolList<AddressAsKey>? GetAccountChanges();

    void ResetTransient();
}
