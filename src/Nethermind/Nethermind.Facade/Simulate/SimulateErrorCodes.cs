// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Facade.Simulate;

public enum SimulateErrorCode
{
    Default = -32000,
    NonceTooHigh = -38011,
    NonceTooLow = -38010,
    IntrinsicGas = -38013,
    InsufficientFunds = -38014,
    BlockGasLimitReached = -38015,
    BlockNumberInvalid = -38020,
    BlockTimestampInvalid = -38021,
    SenderIsNotEOA = -38024,
    MaxInitCodeSizeExceeded = -38025,
    ClientLimitExceeded = -38026,
    InternalError = -32603,
    InvalidParams = -32602,
    Reverted = -32000,
    VMError = -32015,
    TxSyncTimeout = 4,
}
