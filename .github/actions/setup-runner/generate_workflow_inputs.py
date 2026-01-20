#!/usr/bin/env python3
"""
Generate workflow inputs JSON for the runner creation workflow.
This script:
1. Renders the runner setup script from a Jinja2 template
2. Compresses and base64-encodes the setup script
3. Creates the workflow inputs JSON with all required parameters
"""

import base64
import gzip
import json
import os
import sys
from pathlib import Path

try:
    from jinja2 import Environment, FileSystemLoader, select_autoescape
except ImportError:
    print(
        "Error: jinja2 is required. Install with: pip install jinja2", file=sys.stderr
    )
    sys.exit(1)


def get_env_var(name, required=True, default=None):
    """Get an environment variable with optional requirement check."""
    value = os.environ.get(name, default)
    if required and not value:
        print(
            f"Error: Environment variable {name} is required but not set",
            file=sys.stderr,
        )
        sys.exit(1)
    return value


def render_setup_script(template_path, context):
    """Render the setup script from Jinja2 template."""
    template_dir = template_path.parent
    template_file = template_path.name

    env = Environment(
        loader=FileSystemLoader(template_dir),
        autoescape=select_autoescape(),
        trim_blocks=True,
        lstrip_blocks=True,
    )

    template = env.get_template(template_file)
    rendered_script = template.render(context)

    return rendered_script


def encode_script(script_content):
    """Compress with gzip and encode with base64."""
    compressed = gzip.compress(script_content.encode("utf-8"))
    encoded = base64.b64encode(compressed).decode("ascii")
    return encoded


def generate_workflow_inputs():
    """Generate the complete workflow inputs JSON."""

    # Get environment variables
    base_tag = get_env_var("BASE_TAG")
    github_username = get_env_var("GITHUB_USERNAME")
    runner_name = get_env_var("RUNNER_NAME", required=False, default="")
    runner_type = get_env_var("RUNNER_TYPE", required=False, default="")
    runner_labels = get_env_var("RUNNER_LABEL", required=False, default="")
    tags = get_env_var("TAGS", required=False, default="")
    allowed_ips = get_env_var("ALLOWED_IPS", required=False, default="")
    ssh_keys = get_env_var("SSH_KEYS", required=False, default="")
    timeout = get_env_var("TIMEOUT", required=False, default="24")

    # GitHub credentials for runner registration
    gh_token = get_env_var("GH_TOKEN")
    org_name = get_env_var("ORG_NAME")
    repo_name = get_env_var("REPO_NAME")

    # Prepare template context
    template_context = {
        "gh_token": gh_token,
        "org_name": org_name,
        "repo_name": repo_name,
        "runner_name": runner_name,
        "runner_labels": runner_labels,
    }

    # Find template file
    script_dir = Path(__file__).parent
    template_path = script_dir / "runner-setup.sh.j2"

    if not template_path.exists():
        print(f"Error: Template file not found: {template_path}", file=sys.stderr)
        sys.exit(1)

    # Render the setup script
    print(f"Rendering setup script from template: {template_path}", file=sys.stderr)
    setup_script = render_setup_script(template_path, template_context)

    # Encode the setup script
    print("Encoding setup script...", file=sys.stderr)
    encoded_script = encode_script(setup_script)

    # Create workflow inputs JSON
    workflow_inputs = {
        "base_tag": base_tag,
        "github_username": github_username,
        "custom_node_name": runner_name,
        "custom_node_type": runner_type,
        "setup_script": encoded_script,
        "tags": tags,
        "allowed_ips": allowed_ips,
        "ssh_keys": ssh_keys,
        "timeout": timeout,
    }

    return workflow_inputs


def main():
    """Main entry point."""
    try:
        # Generate workflow inputs
        workflow_inputs = generate_workflow_inputs()

        # Write to file
        output_file = get_env_var(
            "OUTPUT_FILE", required=False, default="workflow-inputs.json"
        )
        with open(output_file, "w") as f:
            json.dump(workflow_inputs, f, indent=2)

        print(f"Workflow inputs written to: {output_file}", file=sys.stderr)
        print(
            f"Setup script size: {len(workflow_inputs['setup_script'])} bytes (encoded)",
            file=sys.stderr,
        )

        # Also write to GITHUB_OUTPUT if available
        github_output = os.environ.get("GITHUB_OUTPUT")
        if github_output:
            with open(github_output, "a") as f:
                f.write(f"workflow_inputs_file={output_file}\n")

        return 0

    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        import traceback

        traceback.print_exc()
        return 1


if __name__ == "__main__":
    sys.exit(main())
