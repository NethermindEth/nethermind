# .NET ETH Proofs ZK Verifier

This project serves as a Proof of Concept for a .NET application designed to validate **Ethereum block proofs** aggregated by [Ethproofs.org](https://ethproofs.org/).

**Ethproofs.org** is a block proof explorer for Ethereum that aggregates data from various zkVM (Zero-Knowledge Virtual Machine) teams. It provides a comprehensive overview of proven blocks and allows users to explore proof metadata.

This application acts as a wrapper for various native ZK verifiers implemented in Rust, enabling .NET applications to verify these cryptographic proofs. It supports multiple proof systems (such as OpenVM, Pico, Zisk, etc.) to validate the execution of Ethereum blocks, contributing to the goal of enabling full ZK light clients and validating Ethereum state with a single proof.

The project demonstrates how to bridge .NET and high-performance Rust-based ZK verifiers to interact with the growing ecosystem of Ethereum zkVMs.

This project specifically focuses on multi-GPU ZK verifiers, as they are currently the only ones that provide Real-Time Proving (RTP).

## Important Disclaimer

This project is strictly connected to [Ethproofs.org](https://ethproofs.org/). Due to the dynamic nature of the Ethereum proof ecosystem, `ethproofs.org` frequently updates its provers and verification keys. Therefore, this project requires active maintenance to ensure compatibility and correct functionality with the latest changes from `ethproofs.org`.

## Project Structure

The repository is composed of two main parts:

-   **`src/`**: The main C# .NET project (EthProofValidator). This contains the application logic, models, and interfaces for interacting with the ZK verifiers.
-   **`native-zk-verifier/`**: A Rust project that implements the actual ZK verifier logic for different proof systems (e.g., Zisk, OpenVM, Pico, SP1 Hypercube). The .NET application communicates with this Rust library.

## Building and Running

### Prerequisites

-   .NET SDK (e.g., .NET 10)
-   Rust Toolchain (e.g., `rustup`)

### Build Steps

1.  **Build the .NET Application (includes Rust Verifiers)**:
    Navigate to the root directory of the .NET project and build the C# application. This process will automatically build the Rust verifier library and copy the necessary native libraries into the output directory.

    ```bash
    dotnet build
    ```

### Running the Application

After building both components, you can run the .NET application from the root directory:

```bash
dotnet run
```

This will execute the `Program.cs` which should then utilize the compiled Rust verifiers.

## Demo Behavior

The current implementation in `src/Program.cs` is configured to run a demonstration that validates a sequence of Ethereum blocks.
It defines a `LatestBlockId` validates the preceding `BlockCount` blocks (currently set to 25).
The application sequentially attempts to fetch and validate the proofs for each of these blocks, printing the elapsed time for each validation to the console.


