import copy
import io
import json
from pathlib import Path
import runpy
import subprocess
import tokenize
from types import SimpleNamespace
import unittest
from unittest.mock import Mock


class RelayBootstrapTests(unittest.TestCase):
    def setUp(self):
        self.module = runpy.run_path(str(Path(__file__).with_name("nethermind.star")), init_globals={
            "GetHttpRequestRecipe": SimpleNamespace, "struct": SimpleNamespace,
            "fail": lambda message: (_ for _ in ()).throw(ValueError(message)),
        })
        self.plan = Plan()
        self.config = SimpleNamespace(image="selected-upstream-relay", cmd=[
            "api", "--beacon-uris", "http://builder-beacon:4000,http://other-beacon:4000",
            "--db", "unchanged-database", "--secret-key", "unchanged-relay-key",
        ], ports={"http": 9062}, env_vars={"SEC_PER_SLOT": "12"})

    def test_starlark_module_has_no_python_only_adjacent_string_literals(self):
        source = Path(__file__).with_name("nethermind.star").read_text()
        tokens = [token for token in tokenize.generate_tokens(io.StringIO(source).readline)
                  if token.type not in (tokenize.NL, tokenize.COMMENT)]
        for left, right in zip(tokens, tokens[1:]):
            self.assertFalse(left.type == right.type == tokenize.STRING,
                f"Starlark requires an explicit operator between strings at {right.start}")

    def test_relay_uses_actual_genesis_registry_and_preserves_original_service_config(self):
        command = self.config.cmd.copy()
        overlay = self.module["relay_plan"](self.plan)

        result = overlay.add_service(name="mev-relay-api", config=self.config, description="upstream")

        self.assertIs(result, self.plan.add_service.return_value)
        self.plan.add_service.assert_called_once_with(name="mev-relay-api", config=self.config, description="upstream")
        request = self.plan.request.call_args.kwargs
        self.assertEqual(request["service_name"], "builder-beacon")
        self.assertEqual(request["acceptable_codes"], [200])
        self.assertEqual(request["recipe"].extract, {"pubkeys": self.module["GENESIS_VALIDATORS"]})
        self.assertEqual(request["recipe"].port_id, "http")
        self.assertEqual(request["recipe"].endpoint, "/eth/v1/beacon/states/genesis/validators")
        self.assertEqual(self.config.cmd, command + ["--known-validators", "actual-beacon-pubkeys"])
        self.assertEqual((self.config.image, self.config.ports, self.config.env_vars),
            ("selected-upstream-relay", {"http": 9062}, {"SEC_PER_SLOT": "12"}))

    def test_other_services_are_forwarded_without_beacon_requests_or_command_changes(self):
        command = self.config.cmd.copy()
        self.module["relay_plan"](self.plan).add_service(name="mev-relay-housekeeper", config=self.config)
        self.plan.request.assert_not_called()
        self.plan.add_service.assert_called_once_with(name="mev-relay-housekeeper", config=self.config)
        self.assertEqual(self.config.cmd, command)

    def test_failed_beacon_read_cannot_start_the_relay(self):
        self.plan.request.side_effect = ValueError("beacon unavailable")
        with self.assertRaisesRegex(ValueError, "beacon unavailable"):
            self.module["relay_plan"](self.plan).add_service(name="mev-relay-api", config=self.config)
        self.plan.add_service.assert_not_called()

    def test_registry_is_sorted_by_real_index_and_invalid_registries_are_rejected(self):
        ordered = [{"index": str(i), "validator": {"pubkey": "0x" + f"{i + 1:096x}"}} for i in range(3)]
        valid = {"data": list(reversed(ordered))}
        malformed = [
            {"data": []},
            {"data": ordered[1:]},
            {"data": [ordered[0], ordered[2]]},
            {"data": [ordered[0], ordered[0]]},
            {},
        ]
        duplicate_key = copy.deepcopy(ordered)
        duplicate_key[1]["validator"]["pubkey"] = duplicate_key[0]["validator"]["pubkey"]
        malformed.append({"data": duplicate_key})
        invalid_key = copy.deepcopy(ordered)
        invalid_key[1]["validator"]["pubkey"] = "0x00"
        malformed.append({"data": invalid_key})
        for payload in [valid, *malformed]:
            with self.subTest(payload=payload):
                result = subprocess.run(["jq", "-er", self.module["GENESIS_VALIDATORS"]],
                    input=json.dumps(payload), capture_output=True, text=True, timeout=10)
                if payload == valid:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(result.stdout.strip(), ",".join(item["validator"]["pubkey"] for item in ordered))
                else:
                    self.assertNotEqual(result.returncode, 0)


class Plan:
    def __init__(self):
        self.add_service = Mock()
        self.request = Mock(return_value={"extract.pubkeys": "actual-beacon-pubkeys"})

    def __dir__(self):
        return ["add_service", "request"]
