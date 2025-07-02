// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Evm.T8n.Errors;

public class T8nErrorCodes
{
    public const int ErrorEvm = 2; // Other EVM error
    public const int ErrorConfig = 3; // Failed configuration: when a non-supported or invalid fork was specified.

    public const int ErrorMissingBlockhash = 4; // Block history is not supplied, but needed for a BLOCKHASH operation. If BLOCKHASH is invoked targeting a block which history has not been provided for, the program will exit with code 4.

    public const int ErrorJson = 10; // Invalid input json: the supplied data could not be marshalled
    public const int ErrorIO = 11; // IO problems: failure to load or save files
    public const int ErrorRlp = 12; // Invalid Rlp
}
