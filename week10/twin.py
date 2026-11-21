"""
    python twin.py      ->  http://localhost:1880/dashboard/twin
"""
import webbrowser

from main import HERE, NODE_RED, deploy_flow, node_red_running, start_node_red, stop_node_red
from Coordinator.twin_coordinator import main as run_coordinator

FLOW_FILE = HERE / "NodeRED" / "digital_twin_dashboard.json"
DASHBOARD = NODE_RED + "/dashboard/twin"


def main():
    started = None if node_red_running() else start_node_red()
    try:
        deploy_flow(FLOW_FILE, DASHBOARD)
        webbrowser.open(DASHBOARD)
        run_coordinator()
    finally:
        if started:
            stop_node_red(started)


if __name__ == "__main__":
    main()
