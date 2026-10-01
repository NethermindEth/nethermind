# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import unittest

from validate_profile import validate


class ProfileValidationTests(unittest.TestCase):
    def test_capabilities_are_measured_independently(self):
        method = {"Method": "Caller", "ExclusiveWeight": 10,
                  "CallWeights": [{"Method": "Callee", "Weight": 5}],
                  "InstrumentationData": [{"ILOffset": 0, "InstrumentationKind": "BasicBlockIntCount", "Data": 10}]}
        report = validate({"Methods": [method]}, ("instrumentation", "block-counts", "callweights"))
        self.assertEqual(report["positive_call_edges"], 1)
        self.assertEqual(report["total_exclusive_weight"], 10)
        self.assertEqual(report["instrumented_methods"], 1)
        for missing in ("ExclusiveWeight", "CallWeights", "InstrumentationData"):
            with self.subTest(missing=missing):
                data = {key: value for key, value in method.items() if key != missing}
                requirement = "instrumentation" if missing == "InstrumentationData" else "callweights"
                with self.assertRaisesRegex(ValueError, "missing required"):
                    validate({"Methods": [data]}, (requirement,))

    def test_method_names_alone_do_not_prove_instrumentation(self):
        data = {"Methods": [{"Method": "OnlyName"}]}
        self.assertFalse(validate(data)["capabilities"]["instrumentation"])
        with self.assertRaisesRegex(ValueError, "missing required"):
            validate(data, ("instrumentation",))

    def test_malformed_or_empty_profiles_fail(self):
        cases = ({}, {"Methods": []}, {"Methods": ["name"]},
                 {"Methods": [{"Method": "M", "CallWeights": {}}]},
                 *({"Methods": [{"Method": "M", "ExclusiveWeight": value}]}
                   for value in (-1, True, float("nan"), float("inf"))),
                 {"Methods": [{"Method": "M", "CallWeights": [{"Method": "C"}]}]})
        for data in cases:
            with self.subTest(data=data), self.assertRaises(ValueError):
                validate(data)


if __name__ == "__main__":
    unittest.main()
