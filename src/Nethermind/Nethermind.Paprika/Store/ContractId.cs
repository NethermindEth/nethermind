namespace Paprika.Store;

/// <summary>
/// Represents a contract id.
/// </summary>
/// <remarks>
/// A contract id is an uint uniquely assigned to a contract. It's done by persisting a mapping between  
/// between its address' Keccak and an auto-incremented uint.
/// </remarks>
/// <param name="Value">The underlying value.</param>
public readonly record struct ContractId(uint Value)
{
    private const uint NullValue = 0;
    public bool IsNull => Value == NullValue;

    public static readonly ContractId Null = new(NullValue);
}
