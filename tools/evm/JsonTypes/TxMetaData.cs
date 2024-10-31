// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.JsonTypes;

public class TxMetaData
{
    public bool? Protected { get; set; }
    public byte[]? SecretKey { get; set; }
}
