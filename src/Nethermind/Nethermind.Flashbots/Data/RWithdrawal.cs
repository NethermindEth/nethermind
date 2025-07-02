using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Int256;

public class RWithdrawal
{
    public ulong index { get; set; }
    public ulong validator_index { get; set; }
    public Address address { get; set; }

    [JsonPropertyName("amount")]
    public ulong amount_in_gwei { get; set; }

    public RWithdrawal(Withdrawal withdrawal)
    {
        index = withdrawal.Index;
        validator_index = withdrawal.ValidatorIndex;
        address = withdrawal.Address;
        amount_in_gwei = withdrawal.AmountInGwei;
    }

    public Withdrawal ToWithdrawal()
    {
        return new Withdrawal
        {
            Index = index,
            ValidatorIndex = validator_index,
            Address = address,
            AmountInGwei = amount_in_gwei
        };
    }

    [JsonConstructor]
    public RWithdrawal(
        ulong index,
        ulong validator_index,
        Address address,
        ulong amount_in_gwei
    )
    {
        this.index = index;
        this.validator_index = validator_index;
        this.address = address;
        this.amount_in_gwei = amount_in_gwei;
    }
}
