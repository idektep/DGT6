import json
import shutil
import subprocess
import sys
import time
import urllib.request
import webbrowser
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "Coordinator"))
from Coordinator.line_coordinator import main as run_coordinator 

NODE_RED = "http://localhost:1880"
DASHBOARD = NODE_RED + "/dashboard/line"
FLOW_FILE = HERE / "NodeRED" / "warehouse_line_dashboard.json"
LOG_FILE = HERE / "NodeRED" / "node-red.log"
API = {"Content-Type": "application/json", "Node-RED-API-Version": "v2"}

def node_red_running():
    try:
        urllib.request.urlopen(NODE_RED + "/flows", timeout=2)
        return True
    except OSError:
        return False

def start_node_red():
    exe = shutil.which("node-red")
    if not exe:
        sys.exit("node-red was not found: install it with  npm install -g node-red")
    print(f"Starting Node-RED (log: {LOG_FILE.relative_to(HERE)})")
    process = subprocess.Popen([exe], stdout=open(LOG_FILE, "w"), stderr=subprocess.STDOUT)
    for _ in range(60):
        if node_red_running():
            return process
        time.sleep(1)
    stop_node_red(process)
    sys.exit(f"Node-RED did not start within 60 s: see {LOG_FILE}")

def stop_node_red(process):
    print("Stopping Node-RED")
    if sys.platform == "win32":   # node-red is a .cmd wrapper on Windows: stop the whole process tree
        subprocess.run(["taskkill", "/F", "/T", "/PID", str(process.pid)], capture_output=True)
    else:
        process.terminate()

def deploy_flow(flow_file=FLOW_FILE, dashboard=DASHBOARD):
    flow = json.loads(flow_file.read_text(encoding="utf-8"))
    tab = next(node["id"] for node in flow if node["type"] == "tab")
    page = next(node for node in flow if node["type"] == "ui-page")
    ids = {node["id"] for node in flow}
    current = json.load(urllib.request.urlopen(urllib.request.Request(NODE_RED + "/flows", headers=API)))
    # drop the old copy of this flow, including groups on its page that the new version no longer has
    flows = [node for node in current["flows"]
             if node["id"] not in ids and node.get("z") != tab and node.get("page") != page["id"]]

    bases = [node["id"] for node in flows if node["type"] == "ui-base"]
    if page["ui"] not in bases:
        if not bases:
            sys.exit("Node-RED has no Dashboard 2 yet: add any Dashboard 2 widget in the Node-RED editor once, then retry")
        page["ui"] = bases[0]

    body = json.dumps({"rev": current["rev"], "flows": flows + flow}).encode()
    request = urllib.request.Request(NODE_RED + "/flows", data=body, method="POST",
                                     headers={**API, "Node-RED-Deployment-Type": "full"})
    urllib.request.urlopen(request)
    print(f"Dashboard deployed: {dashboard}")

def main():
    started = None if node_red_running() else start_node_red()
    try:
        deploy_flow()
        webbrowser.open(DASHBOARD)
        run_coordinator()
    finally:
        if started:
            stop_node_red(started)


if __name__ == "__main__":
    main()
