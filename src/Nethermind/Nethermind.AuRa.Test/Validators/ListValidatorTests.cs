using System.Numerics;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class ListValidatorTests
    {
        private const string Include1 = "0xffffffffffffffffffffffffffffffffffffffff";
        private const string Include2 = "0xfffffffffffffffffffffffffffffffffffffffe";
        
        [TestCase(Include1, ExpectedResult = true)]
        [TestCase(Include2, ExpectedResult = true)]
        [TestCase("0xAAfffffffffffffffffffffffffffffffffffffe", ExpectedResult = false)]
        [TestCase("0xfffffffffffffffffffffffffffffffffffffffd", ExpectedResult = false)]
        public bool Should_validate_correctly(string address)
        {
            var validator = new ListValidator(
                new AuRaParameters.Validator()
                {
                    Addresses = new[] {new Address(Include1), new Address(Include2), }
                });

            return validator.IsValidSealer(new Address(address));
        }
    }
}