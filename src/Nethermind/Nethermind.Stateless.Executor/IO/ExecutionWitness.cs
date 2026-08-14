// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial struct ExecutionWitness
{
    [SszList(0x40_0000)]
    public SszWitnessState[] State { get; set; }

    [SszList(0x4_0000)]
    public SszWitnessCodes[] Codes { get; set; }

    [SszList(0x100)]
    public SszWitnessHeader[] Headers { get; set; }

    public static ExecutionWitness From(Witness witness)
    {
        SszWitnessCodes[] codes = new SszWitnessCodes[witness.Codes.Count];

        for (int i = 0; i < codes.Length; i++)
            codes[i] = new() { Bytes = witness.Codes[i] };

        SszWitnessHeader[] headers = new SszWitnessHeader[witness.Headers.Count];

        for (int i = 0; i < headers.Length; i++)
            headers[i] = new() { Bytes = witness.Headers[i] };

        SszWitnessState[] state = new SszWitnessState[witness.State.Count];

        for (int i = 0; i < state.Length; i++)
            state[i] = new() { Bytes = witness.State[i] };

        return new()
        {
            Codes = codes,
            Headers = headers,
            State = state
        };
    }

    public readonly Witness ToWitness()
    {
        ArrayPoolList<byte[]> state = new(State.Length, State.Length);

        for (int i = 0; i < State.Length; i++)
            state[i] = State[i].Bytes;

        ArrayPoolList<byte[]> codes = new(Codes.Length, Codes.Length);

        for (int i = 0; i < Codes.Length; i++)
            codes[i] = Codes[i].Bytes;

        ArrayPoolList<byte[]> headers = new(Headers.Length, Headers.Length);

        for (int i = 0; i < Headers.Length; i++)
            headers[i] = Headers[i].Bytes;

        return new()
        {
            Codes = codes,
            Headers = headers,
            Keys = ArrayPoolList<byte[]>.Empty(),
            State = state
        };
    }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessCodes
{
    [SszList(0x1_0000)]
    public byte[] Bytes { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessHeader
{
    [SszList(0x400)]
    public byte[] Bytes { get; set; }
}

[SszContainer(isCollectionItself: true)]
public partial struct SszWitnessState
{
    [SszList(0x400)]
    public byte[] Bytes { get; set; }
}
