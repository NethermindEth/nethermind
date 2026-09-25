#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from pathlib import Path
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


class SolutionProjectReferencesTests(unittest.TestCase):
    def test_main_solution_includes_literal_project_references(self):
        solution = ROOT / "src/Nethermind/Nethermind.slnx"
        projects = {
            (solution.parent / node.attrib["Path"]).resolve()
            for node in ET.parse(solution).iter("Project")
        }
        for project in sorted(projects):
            for reference in ET.parse(project).iter("ProjectReference"):
                include = reference.get("Include", "")
                if not include or "$" in include:
                    continue
                dependency = (project.parent / include.replace("\\", "/")).resolve()
                with self.subTest(project=project.name, dependency=dependency.name):
                    self.assertIn(
                        dependency,
                        projects,
                        f"{project.name} references {dependency.name} outside the solution; "
                        "include it so Visual Studio can load and restore it.",
                    )


if __name__ == "__main__":
    unittest.main()
