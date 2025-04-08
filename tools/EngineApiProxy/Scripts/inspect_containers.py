import subprocess
import json

def get_docker_ps():
    """Run `docker ps` and return the output."""
    result = subprocess.run(["docker", "ps", "--format", "{{.ID}} {{.Names}}"], capture_output=True, text=True)
    return result.stdout.strip().split("\n")

def get_container_details(container_id):
    """Run `docker inspect` and return the container details as a dictionary."""
    result = subprocess.run(["docker", "inspect", container_id], capture_output=True, text=True)
    return json.loads(result.stdout)

def get_container_ip(details):
    """Retrieve the IP address from the container details."""
    network_settings = details[0].get("NetworkSettings", {})
    ip_address = network_settings.get("IPAddress")
    
    # Check custom networks if default IPAddress is empty
    if not ip_address:
        networks = network_settings.get("Networks", {})
        for network_name, network_info in networks.items():
            ip_address = network_info.get("IPAddress")
            if ip_address:
                break
    return ip_address

def get_execution_endpoints(details):
    """Retrieve the execution endpoints from the container's Args."""
    args = details[0].get("Args", [])
    for arg in args:
        if arg.startswith("--execution-endpoints="):
            return arg.split("=", 1)[1]
    return None

def get_beacon_nodes(details):
    """Retrieve the beacon nodes from the container's Args."""
    args = details[0].get("Args", [])
    for arg in args:
        if arg.startswith("--beacon-nodes="):
            return arg.split("=", 1)[1]
    return None

def get_el_proxy_args(details):
    """Retrieve and transform arguments for 'el-proxy-' containers."""
    args = details[0].get("Args", [])
    transformed_args = {}
    for arg in args:
        if arg.startswith("--"):
            if "=" in arg:
                key, value = arg.split("=", 1)
            else:
                key, value = arg, None  # Handle arguments without `=`
            transformed_key = key.lstrip("-").replace("-", "_").upper()
            transformed_args[transformed_key] = value
    return transformed_args

def main():
    # Step 1: Get all running containers
    containers = get_docker_ps()

    # Step 2: Filter containers starting with "cl-1", "cl-2", "vc-", or "el-proxy-"
    seen_containers = set()
    matching_containers = []
    for container in containers:
        container_id, container_name = container.split(" ", 1)
        if (
            container_name.startswith("cl-")
            or (container_name.startswith("el-") and not container_name.startswith("el-proxy-"))
            or container_name.startswith("vc-")
            or container_name.startswith("el-proxy-")
        ):
            if container_id not in seen_containers:
                matching_containers.append((container_id, container_name))
                seen_containers.add(container_id)

    # Step 3: Inspect each matching container
    for container_id, container_name in matching_containers:
        details = get_container_details(container_id)
        ip_address = get_container_ip(details)
        execution_endpoints = get_execution_endpoints(details)
        beacon_nodes = get_beacon_nodes(details) if container_name.startswith("vc-") else None
        el_proxy_args = get_el_proxy_args(details) if container_name.startswith("el-proxy-") else None

        # Print the results
        print(f"Container Name: {container_name}")
        print(f"Container ID: {container_id}")
        print(f"IP Address: {ip_address if ip_address else 'Not Found'}")
        if container_name.startswith("cl-"):
            print(f"Execution Endpoints: {execution_endpoints if execution_endpoints else 'Not Found'}")
        if container_name.startswith("vc-"):
            print(f"Beacon Nodes: {beacon_nodes if beacon_nodes else 'Not Found'}")
        if container_name.startswith("el-proxy-"):
            print("Transformed Args:")
            for key, value in el_proxy_args.items():
                print(f"  {key}: {value}")
        print("-" * 40)

if __name__ == "__main__":
    main()