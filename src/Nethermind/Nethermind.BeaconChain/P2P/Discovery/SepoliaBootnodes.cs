// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.P2P.Discovery;

/// <summary>Built-in Sepolia testnet consensus-layer bootnode ENRs.</summary>
/// <remarks>
/// Copied verbatim from the eth-clients/sepolia registry
/// (https://github.com/eth-clients/sepolia/blob/main/metadata/bootstrap_nodes.yaml), the maintained
/// source of truth this network's clients sync their own built-in lists from. Every entry was
/// cross-checked against a second, independent source as of 2026-09-20: the five EF/NodeOps entries
/// against OffchainLabs/prysm PR #17474 (merged 2026-09-11, the matching bootnode-fleet swap), and the
/// Teku, Lodestar and two unlabelled entries against both sigp/lighthouse's built-in Sepolia config and
/// that same Prysm file, all of which still carry them verbatim.
/// </remarks>
public static class SepoliaBootnodes
{
    public static readonly string[] Enrs =
    [
        // EF (NodeOps fleet, replaces the retired EF bootnodes per eth-clients/sepolia#124)
        "enr:-KG4QCK5YeEoL55e2hoS6nCregwx0Zd6NQ3rhVDfeg5Q8ozUNmUYTskpmuqo2WYFo3z24-cWC9qrU3yYKDSJ299lh8sBgmlkgnY0gmlwhNRj2kKDaXA2kCoAHKALAA0CAAAAAAAAAF6Jc2VjcDI1NmsxoQLzqnTxu_nlM8V_semraAjfbH9HZpcbVUCXH2qanVPsroN1ZHCCTruEdWRwNoJOuw",
        "enr:-KG4QF0FvRL7Eqc4oURFhOkS0V6guntLnw54dYgTruM7z9TAMWhRpCrxZ7Pd536-q4qlwdW13czht8_UEWwGyJesu1gBgmlkgnY0gmlwhIHUpj2DaXA2kCYEqIAABAHQAAAAA2GdUACJc2VjcDI1NmsxoQL5iA7gNCs4SDmnXz8Isacq0EJbJfvV_uJlccoHxHU5ZYN1ZHCCI4yEdWRwNoIjjA",
        "enr:-KG4QI4reJ1D_BwCwg6EKAuo2HEWoIVVNjphtOTJP2gzPVLSTYM3NFwp39TAKw-7QiQ2NVts7DK4rjJR2BEcAwh3BckBgmlkgnY0gmlwhJB-_BiDaXA2kCQAYYABAADQAAAAAYEgYAGJc2VjcDI1NmsxoQLXzHa5K0M3F4pqErIhleMByA8votAUhUXylRT6SWX2HoN1ZHCCI4yEdWRwNoIjjA",
        "enr:-KG4QI1KOrogxK8u3Oc0QLdgkNTbAPuAMtixa6Vx05N-Bl7IOCVURUvqZ2N6JA97ts7YG1B4D3hQvZ9uQlCPYVjy1DABgmlkgnY0gmlwhLKc14yDaXA2kCoBBP8A9DxKAAAAAAAAAAGJc2VjcDI1NmsxoQLB0ZhHGRmVwXja_4o-GRN1VVJYRI11F45CTAlu1s00Q4N1ZHCCI4yEdWRwNoIjjA",
        "enr:-KG4QKU4YfXfB3_BVI7u0VvXnSJI6cqo-tCRm-Ggh3XxBImcYvrUoKUDbIjJjG9-QphuH6gzScdf69t597M0nHut4kABgmlkgnY0gmlwhAXfXlGDaXA2kCoBBP8C8BytAAAAAAAAAAGJc2VjcDI1NmsxoQL0y83XKpPgvY7XReWg9S8bdI2UUIe5dE0N7rjOIIj4xYN1ZHCCI4yEdWRwNoIjjA",
        // Teku
        "enr:-Iu4QKvMF7Ne_RSQoZGvavTuZ1QA5_Pgeb0nq_hrjhU8s0UDV3KhcMXJkGwOWhsDGZL3ISjL0CTP-hfoTjZtEtCEwR4BgmlkgnY0gmlwhAOAaySJc2VjcDI1NmsxoQNta5b_bexSSwwrGW2Re24MjfMntzFd0f2SAxQtMj3ueYN0Y3CCIyiDdWRwgiMo",
        // Lodestar
        "enr:-KG4QJejf8KVtMeAPWFhN_P0c4efuwu1pZHELTveiXUeim6nKYcYcMIQpGxxdgT2Xp9h-M5pr9gn2NbbwEAtxzu50Y8BgmlkgnY0gmlwhEEVkQCDaXA2kCoBBPnAEJg4AAAAAAAAAAGJc2VjcDI1NmsxoQLEh_eVvk07AQABvLkTGBQTrrIOQkzouMgSBtNHIRUxOIN1ZHCCIyiEdWRwNoIjKA",
        // Unlabelled in the source registry
        "enr:-Iq4QMCTfIMXnow27baRUb35Q8iiFHSIDBJh6hQM5Axohhf4b6Kr_cOCu0htQ5WvVqKvFgY28893DHAg8gnBAXsAVqmGAX53x8JggmlkgnY0gmlwhLKAlv6Jc2VjcDI1NmsxoQK6S-Cii_KmfFdUJL2TANL3ksaKUnNXvTCv1tLwXs0QgIN1ZHCCIyk",
        "enr:-L64QC9Hhov4DhQ7mRukTOz4_jHm4DHlGL726NWH4ojH1wFgEwSin_6H95Gs6nW2fktTWbPachHJ6rUFu0iJNgA0SB2CARqHYXR0bmV0c4j__________4RldGgykDb6UBOQAABx__________-CaWSCdjSCaXCEA-2vzolzZWNwMjU2azGhA17lsUg60R776rauYMdrAz383UUgESoaHEzMkvm4K6k6iHN5bmNuZXRzD4N0Y3CCIyiDdWRwgiMo",
    ];
}
