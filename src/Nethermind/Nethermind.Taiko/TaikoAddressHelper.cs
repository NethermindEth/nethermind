using Nethermind.Core.Specs;
using Nethermind.Core;

namespace Nethermind.Taiko;

static class TaikoAddressHelper
{
    private const string TaikoL2AddressSuffix = "10001";

    public static Address GetTaikoL2ContractAddress(ISpecProvider specProvider) => new(
        specProvider.ChainId.ToString().PadRight(40 - TaikoL2AddressSuffix.Length, '0') + TaikoL2AddressSuffix
        );
}
