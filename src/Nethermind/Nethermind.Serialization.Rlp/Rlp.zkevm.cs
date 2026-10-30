// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    public static Rlp OfEmptyByteArray => field ??= new(EmptyByteArrayByte);

    public static Rlp OfZero => OfEmptyByteArray;

    public static Rlp OfEmptyList => field ??= new(EmptyListByte);

    internal static Rlp OfEmptyTreeHash => field ??= Encode(Keccak.EmptyTreeHash.Bytes);

    internal static Rlp OfEmptyStringHash => field ??= Encode(Keccak.OfAnEmptyString.Bytes);

    internal static Rlp EmptyBloom => field ??= Encode(Bloom.Empty.Bytes);

    private static Dictionary<RlpDecoderKey, IRlpDecoder> _decoderBuilder
    {
        get
        {
            if (field is null)
            {
                field = [];
                RegisterDefaultDecoders();
            }

            return field;
        }
    }

    private static Lock _decoderLock => field ??= new();

    private static ILogger _logger => Static.LogManager.GetClassLogger<Rlp>();

    private static Dictionary<RlpDecoderKey, IRlpDecoder>? _decodersSnapshot;

    public static Dictionary<RlpDecoderKey, IRlpDecoder> Decoders
    {
        get
        {
            Dictionary<RlpDecoderKey, IRlpDecoder>? snapshot = Volatile.Read(ref _decodersSnapshot);
            return snapshot ?? CreateDecodersSnapshot();
        }
    }

    private static Dictionary<RlpDecoderKey, IRlpDecoder> CreateDecodersSnapshot()
    {
        using Lock.Scope _ = _decoderLock.EnterScope();
        Dictionary<RlpDecoderKey, IRlpDecoder>? snapshot = _decodersSnapshot;
        if (snapshot is null)
        {
            snapshot = new Dictionary<RlpDecoderKey, IRlpDecoder>(_decoderBuilder);
            Volatile.Write(ref _decodersSnapshot, snapshot);
        }

        return snapshot;
    }

    public static partial void RegisterDecoders(Assembly assembly, bool canOverrideExistingDecoders) => RegisterDefaultDecoders();

    private static bool RegisterDefaultDecoders()
    {
        // Under zkEVM/bflat AOT we cannot rely on reflection-based auto-discovery of decoders
        // (CustomAttribute instantiation can trigger TypeLoader failures).
        // Register the required decoders explicitly instead.
        RegisterDecoder(typeof(Account), AccountDecoder.Instance);
        RegisterDecoder(typeof(AuthorizationTuple), new AuthorizationTupleDecoder());
        RegisterDecoder(typeof(Hash256), KeccakDecoder.Instance);
        RegisterDecoder(typeof(LogEntry), LogEntryDecoder.Instance);
        RegisterDecoder(typeof(Transaction), TxDecoder.Instance);
        RegisterDecoder(typeof(Withdrawal), new WithdrawalDecoder());

        RegisterPendingDecoders();
        return true;
    }
}

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() =>
        SpanExtensions.CombineHash((uint)_type.GetHashCode(), (uint)MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
