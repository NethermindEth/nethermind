using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Serialization.Rlp
{
    // attribute in case two or more decoders exit for the same type
    internal class PreferenceDecoderAttribute : Attribute
    {
        // description for the attribute
        public string Description { get; set; }
        // priority setting to compare with other decoder
        public byte priotity { get; set; }

        public PreferenceDecoderAttribute(string description, byte priotity)
        {
            Description = description;
            this.priotity = priotity;
        }
    }
}
