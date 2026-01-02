# <center>Lantern.Discv5</center>

Lantern.Discv5, written in C#, is a library that provides an implementation of the [Node Discovery V5](https://github.com/ethereum/devp2p/blob/master/discv5/discv5.md) protocol. 

This implementation provides relevant functionalities to allow peer-to-peer communication and facilitate node discovery in applications built on the .NET platform.

## Features
The provided implementation has the following features available:

- Enables discovery of nodes that support the [ENR (Ethereum Node Record)](https://eips.ethereum.org/EIPS/eip-778) format
- Encrypted communication between peers using Secp256k1 and Diffie-Hellman key exchange protocol
- Provides functionality to enumerate the entire DHT in the network, leveraging lookup features for targeted nodes via independent path buckets
- Support for implementing custom handling of application-level request and response using `TALKREQ` and `TALKRESP` messages

## Installation

*Note: These instructions assume you are familiar with the .NET Core development environment. If not, please refer to the [official documentation](https://docs.microsoft.com/en-us/dotnet/core/introduction) to get started.*

1. Install [.NET Core SDK](https://docs.microsoft.com/en-us/dotnet/core/install/) on your system if you haven't already.

2. Clone the repository:

   ```bash
   git clone https://github.com/Pier-Two/Lantern.Discv5.git
   ```

3. Change to the `Lantern.Discv5` directory:

   ```bash
   cd Lantern.Discv5
   ```

4. Build the project:

   ```bash
   dotnet build
   ```

5. Execute tests:
   ```bash
   dotnet test
   ```

## Quick Usage

This library can be used in any C# project using the following import statement: 

```
using Lantern.Discv5.WireProtocol;
```

You can initialize the protocol by providing an array of bootstrap ENR ([Ethereum Node Record](https://eips.ethereum.org/EIPS/eip-778)) strings:

```
Discv5Protocol discv5 = Discv5Builder.CreateDefault(bootstrapEnrs);
```

Please refer to the [Usage](https://piertwo.gitbook.io/lantern.discv5/) guide for more information and examples of the bootstrap ENRs as well as a comprehensive understanding of the available functionalities and configuration options.


## Contributing

We welcome contributions to the Lantern.Discv5 project. To get involved, please read our [Contributing Guidelines](https://piertwo.gitbook.io/lantern.discv5/contribution-guidelines) for the process for submitting pull requests to this repository.

## License
This project is licensed under the [MIT License](https://github.com/Pier-Two/Lantern.Discv5/blob/main/LICENSE).
