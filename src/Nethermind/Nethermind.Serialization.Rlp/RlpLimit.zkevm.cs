// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Serialization.Rlp;

// Computed on access, so the guest runs no class constructor for this type.
public partial record struct RlpLimit
{
    public static RlpLimit DefaultLimit => new();
    public static RlpLimit Bloom => new(Core.Bloom.ByteLength, nameof(Core.Bloom));
    public static RlpLimit L4 => new(4);
    public static RlpLimit L8 => new(8);
    public static RlpLimit L32 => new(32);
    public static RlpLimit L64 => new(64);
    public static RlpLimit L65 => new(65);

    private static RlpLimit? _receiptLogs;

    public static RlpLimit ReceiptLogs
    {
        get => _receiptLogs ??= ForReceiptLogs(MaxBlockGas);
        private set => _receiptLogs = value;
    }
}
