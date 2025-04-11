import argparse
import subprocess
import sys

def run_command(command):
    """Run a shell command and return its output."""
    result = subprocess.run(command, shell=True, capture_output=True, text=True)
    if result.returncode != 0:
        print(f"Error running command: {command}")
        print(result.stderr)
        sys.exit(1)
    return result.stdout.strip()

def restart_container_with_env(file_location, env_var_name, env_var_value):
    """Restart the container using docker-compose with an overridden environment variable."""
    print(f"Restarting the container with {env_var_name}={env_var_value}...")

    # Stop the container
    print("Stopping the container...")
    run_command(f"docker-compose -f {file_location} down")

    # Start the container with the overridden environment variable
    print("Starting the container with the overridden environment variable...")
    run_command(f"{env_var_name}={env_var_value} docker-compose -f {file_location} up -d")

    print(f"Container restarted with {env_var_name}={env_var_value}")

def main():
    # Use argparse to handle command-line arguments
    parser = argparse.ArgumentParser(description="Restart a Docker container with an overridden environment variable.")
    parser.add_argument("--file", required=True, help="Path to the docker-compose file.")
    parser.add_argument("--variable", required=True, help="Name of the environment variable to override.")
    parser.add_argument("--value", required=True, help="New value for the environment variable.")

    args = parser.parse_args()

    # Restart the container with the overridden environment variable
    restart_container_with_env(args.file, args.variable, args.value)

if __name__ == "__main__":
    main()