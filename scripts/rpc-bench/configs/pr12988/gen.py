"""Generates json-bench workloads for benchmarking PR #12988 (EVM admission gate).

Every call targets `latest` on the rpc-bench mainnet snapshot (block 25,490,000) and
uses long-lived mainnet contracts. Configs are written as JSON, which YAML loaders accept.
"""
import json
import os
import yaml
from Crypto.Hash import keccak

OUT = os.path.join(os.path.dirname(__file__), "configs")
os.makedirs(OUT, exist_ok=True)

WETH = "0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2"
USDC = "0xA0b86991c6218b36c1d19D4a2e9Eb0cE3606eB48"
DAI = "0x6B175474E89094C44Da98b954EedeAC495271d0F"
UNI_V2_ROUTER = "0x7a250d5630B4cF539739dF2C5dAcb4c659F2488D"
UNI_V3_QUOTER_V2 = "0x61fFE014bA17989E743c5F6cB21bF9697530B21e"
# Candidate ETH-rich EOAs; calibration shows which still hold funds at the snapshot head.
WHALES = {
    "binance8": "0xF977814e90dA44bFA03b6295A0616a897441aceC",
    "robinhood": "0x40B38765696e3d5d8d9d834D8AaD4bB6e418E489",
}
# Fresh address funded only through state overrides.
SIM = "0x1000000000000000000000000000000000000abc"
RECIPIENT = "0x2000000000000000000000000000000000000def"
ONE_ETH = 10**18


def word(v):
    if isinstance(v, str):
        return v.lower().removeprefix("0x").rjust(64, "0")
    return format(v, "064x")


def calldata(selector, *words):
    return "0x" + selector.removeprefix("0x") + "".join(words)


def keccak_hex(data_hex):
    k = keccak.new(digest_bits=256)
    k.update(bytes.fromhex(data_hex))
    return "0x" + k.hexdigest()


def mapping_slot(key, slot):
    return keccak_hex(word(key) + word(slot))


def swap_eth_for_usdc(to):
    # swapExactETHForTokens(uint256 amountOutMin, address[] path, address to, uint256 deadline)
    return calldata("7ff36ab5", word(0), word(0x80), word(to), word(2**64), word(2), word(WETH), word(USDC))


DEPOSIT = "0xd0e30db0"
TRANSFER_HALF = calldata("a9059cbb", word(RECIPIENT), word(ONE_ETH // 2))
# QuoterV2.quoteExactInputSingle((WETH, USDC, 1 ETH, fee 500, sqrtPriceLimitX96 0))
QUOTE = calldata("c6a5026a", word(WETH), word(USDC), word(ONE_ETH), word(500), word(0))
FUNDED = {SIM: {"balance": hex(10**22)}}
WETH_FUNDED = {
    SIM: {"balance": hex(10**22)},
    WETH: {"stateDiff": {mapping_slot(SIM, 3): "0x" + word(10**21)}},
}


def call(name, method, params, weight=1):
    return {"name": name, "method": method, "params": params, "weight": weight}


def light_calls():
    whale = WHALES["binance8"]
    return [
        call("eth_call/usdc-balanceOf", "eth_call", [{"to": USDC, "data": calldata("70a08231", word(whale))}, "latest"]),
        call("eth_call/weth-totalSupply", "eth_call", [{"to": WETH, "data": "0x18160ddd"}, "latest"]),
        call("eth_call/dai-balanceOf", "eth_call", [{"to": DAI, "data": calldata("70a08231", word(whale))}, "latest"]),
    ]


def gated_calls(whale_key):
    whale = WHALES[whale_key]
    swap = {"from": whale, "to": UNI_V2_ROUTER, "value": hex(ONE_ETH), "data": swap_eth_for_usdc(whale)}
    # geth-style defaults put the block gas limit on an access-list request, which the sender cannot fund.
    swap_with_gas = dict(swap, gas=hex(300000))
    simulate_block = {
        "stateOverrides": FUNDED,
        "calls": [
            {"from": SIM, "to": WETH, "value": hex(ONE_ETH), "data": DEPOSIT},
            {"from": SIM, "to": WETH, "data": TRANSFER_HALF},
            {"from": SIM, "to": UNI_V2_ROUTER, "value": hex(ONE_ETH), "data": swap_eth_for_usdc(SIM)},
        ],
    }
    return [
        call("eth_call/v3-quote", "eth_call", [{"to": UNI_V3_QUOTER_V2, "data": QUOTE}, "latest"]),
        call("eth_call/v2-swap-override", "eth_call",
             [{"from": SIM, "to": UNI_V2_ROUTER, "value": hex(ONE_ETH), "data": swap_eth_for_usdc(SIM)}, "latest", FUNDED]),
        call(f"eth_estimateGas/weth-deposit-{whale_key}", "eth_estimateGas",
             [{"from": whale, "to": WETH, "value": hex(ONE_ETH), "data": DEPOSIT}, "latest"]),
        call(f"eth_estimateGas/v2-swap-{whale_key}", "eth_estimateGas", [swap, "latest"]),
        call("eth_estimateGas/weth-transfer-override", "eth_estimateGas",
             [{"from": SIM, "to": WETH, "data": TRANSFER_HALF}, "latest", WETH_FUNDED]),
        call(f"eth_createAccessList/v2-swap-{whale_key}", "eth_createAccessList", [swap_with_gas, "latest"]),
        call("eth_createAccessList/usdc-balanceOf", "eth_createAccessList",
             [{"from": whale, "to": USDC, "gas": hex(100000), "data": calldata("70a08231", word(whale))}, "latest"]),
        call("eth_simulateV1/deposit-transfer-swap", "eth_simulateV1",
             [{"blockStateCalls": [simulate_block], "validation": False, "traceTransfers": False}, "latest"]),
        call("eth_simulateV1/two-blocks", "eth_simulateV1",
             [{"blockStateCalls": [simulate_block, {"calls": simulate_block["calls"][1:]}], "validation": False}, "latest"]),
        call(f"eth_fillTransaction/transfer-{whale_key}", "eth_fillTransaction", [{"from": whale, "to": RECIPIENT, "value": "0x1"}]),
        call(f"eth_fillTransaction/weth-deposit-{whale_key}", "eth_fillTransaction",
             [{"from": whale, "to": WETH, "value": hex(ONE_ETH), "data": DEPOSIT}]),
    ]


def heavy_multicalls():
    calls = []
    for n in (1, 2):
        src = yaml.safe_load(open(os.path.join(os.path.dirname(__file__), f"multicall-high-gas-{n}.src.yaml")))
        c = src["calls"][0]
        calls.append(call(c["name"], c["method"], c["params"]))
    return calls


def write(name, test_name, calls, rps=10, duration="60s", vus=2000):
    cfg = {"test_name": test_name, "description": "PR #12988 admission-gate benchmark workload",
           "clients": ["nethermind"], "duration": duration, "rps": rps, "vus": vus, "calls": calls}
    with open(os.path.join(OUT, name), "w", newline="\n") as f:
        json.dump(cfg, f, indent=1)
        f.write("\n")


# Calibration: every candidate call once per whale, equal weight, low rate.
calib = light_calls() + heavy_multicalls()
for key in WHALES:
    for c in gated_calls(key):
        if all(c["name"] != existing["name"] for existing in calib):
            calib.append(c)
write("calib.yaml", "PR #12988 calibration", calib)


def weighted(calls, weight):
    return [dict(c, weight=weight) for c in calls]


gated = gated_calls("binance8")
by_method = {}
for c in gated:
    by_method.setdefault(c["method"], []).append(c)
gated_extra = [c for c in gated_calls("robinhood") if c["method"] in ("eth_fillTransaction", "eth_estimateGas") and "override" not in c["name"]]

# Mixed traffic over every gated method plus light, never-gated-before plain reads.
mix = weighted(light_calls(), 10) + weighted(by_method["eth_call"], 10) + weighted(by_method["eth_estimateGas"], 5)     + weighted(by_method["eth_createAccessList"], 5) + weighted(by_method["eth_simulateV1"], 5) + weighted(by_method["eth_fillTransaction"], 5)
write("gated-mix.yaml", "PR #12988 gated methods + light reads", mix)
for method, calls in by_method.items():
    if method == "eth_call":
        continue
    extra = [c for c in gated_extra if c["method"] == method]
    write(f"iso-{method.removeprefix('eth_').lower()}.yaml", f"PR #12988 {method} isolated", calls + extra)

# The heaviest Multicall3 calls competing with light reads: do light calls still get through?
write("multicall-contention.yaml", "PR #12988 heavy multicall + light reads",
      weighted(heavy_multicalls(), 1) + weighted(light_calls(), 2))

for f in sorted(os.listdir(OUT)):
    cfg = json.load(open(os.path.join(OUT, f)))
    print(f"{f:32s} {len(cfg['calls']):3d} calls  total weight {sum(c['weight'] for c in cfg['calls'])}")
