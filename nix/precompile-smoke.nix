# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

# Calls the precompiles backed by native libraries (ecAdd -> libmcl, P256VERIFY -> libsecp256r1) on a
# Nix-built node. Its own ports keep it apart from the smoke check where the sandbox shares the loopback.
{
  jq,
  nethermind,
  nodeCheck,
}:
nodeCheck {
  name = "nethermind-precompile-smoke-check";
  nodeArgs = "-c spaceneth --Init.ChainSpecPath \"$TMPDIR/chainspec.json\" --Network.P2PPort 30313 --Network.DiscoveryPort 30313";
  chainId = "0x63";
  rpcPort = 18545;
  extraInputs = [ jq ];
  setup = ''
    chainspec=$(find ${nethermind}/ -path '*chainspec/spaceneth.json' | head -n 1)
    [ -n "$chainspec" ] || fail "spaceneth chainspec not found"
    jq '.params.eip7951TransitionTimestamp = "0x0"' "$chainspec" > chainspec.json
  '';
  assertions = ''
    one=0000000000000000000000000000000000000000000000000000000000000001
    two=0000000000000000000000000000000000000000000000000000000000000002
    ecadd=$(rpc "{\"jsonrpc\":\"2.0\",\"method\":\"eth_call\",\"params\":[{\"to\":\"0x0000000000000000000000000000000000000006\",\"data\":\"0x$one$two$one$two\"},\"latest\"],\"id\":2}") \
      || fail "ecAdd request failed"
    echo "ecAdd response: $ecadd"
    echo "$ecadd" | grep -qF '"result":"0x030644e72e131a029b85045b68181585d97816a916871ca8d3c208c16d87cfd315ed738c0e0a7c92e7845f96b2ae9c0a68a6a449e3538fc7ff3ebf7a5a18a2c4"' \
      || fail "unexpected ecAdd response"

    p256=bb5a52f42f9c9261ed4361f59422a1e30036e7c32b270c8807a419feca6050232ba3a8be6b94d5ec80a6d9d1190a436effe50d85a1eee859b8cc6af9bd5c2e184cd60b855d442f5b3c7b11eb6c4e0ae7525fe710fab9aa7c77a67f79e6fadd762927b10512bae3eddcfe467828128bad2903269919f7086069c8c4df6c732838c7787964eaac00e5921fb1498a60f4606766b3d9685001558d1a974e7341513e
    p256verify=$(rpc "{\"jsonrpc\":\"2.0\",\"method\":\"eth_call\",\"params\":[{\"to\":\"0x0000000000000000000000000000000000000100\",\"data\":\"0x$p256\"},\"latest\"],\"id\":3}") \
      || fail "P256VERIFY request failed"
    echo "P256VERIFY response: $p256verify"
    echo "$p256verify" | grep -qF "\"result\":\"0x$one\"" || fail "unexpected P256VERIFY response"
  '';
}
