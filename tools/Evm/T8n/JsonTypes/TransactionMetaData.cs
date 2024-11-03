// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8n.JsonTypes;

public class TransactionMetaData
{
    public bool? Protected { get; set; }
    public byte[]? SecretKey { get; set; }
}
