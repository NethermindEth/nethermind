namespace Nethermind.EraE;

public class Proof
{
    public Proof(ProofType type, byte[] data)
    {
        Type = type;
        Data = data;
    }
}