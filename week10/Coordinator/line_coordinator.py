"""
Topics (all on broker.emqx.io:1883):
  dgt/warehouse/line/ui            <- Node-RED buttons: start, next, retry, reset, empty_shelf,
                                      mode_step, mode_full, target_sim, target_both
  dgt/warehouse/line/command       -> Unity: start, next, reset, mode_step, empty_shelf
  dgt/warehouse/line/state         <- Unity state (JSON)
  dgt/warehouse/line/real/command  -> real system: {request_id, step, action, item_id}  (step 0 + action "reset" = Reset)
  dgt/warehouse/line/real/state    <- real system: {request_id, step, status: running|done|error, message}
  dgt/warehouse/line/report        -> Node-RED: everything the dashboard shows, Unity's state included (JSON, retained)
"""
import json
import re
import threading
import time
from datetime import datetime

BROKER, PORT = "broker.emqx.io", 1883
BASE = "dgt/warehouse/line"
UI = BASE + "/ui"
SIM_COMMAND = BASE + "/command"
SIM_STATE = BASE + "/state"
REAL_COMMAND = BASE + "/real/command"
REAL_STATE = BASE + "/real/state"
REPORT = BASE + "/report"

ACTIONS = ["conveyor_to_arm2", "arm2_pick", "arm2_place_on_agv", "agv_to_arm1",
           "arm1_pick", "arm1_place_on_cartesian", "cartesian_store", "agv_cartesian_home"]
RESET_UP_TO_STEP = 4  
TIMEOUT_S = 60
# ---------------------------------------------------------------------- state
def new_line():
    return {
        "waiting_for": "start",   # start | sim | real | user | error
        "mode": "step",           # step | full
        "target": "sim",          # sim | both: the real system only moves when the user chooses it
        "step": 0,
        "seq": 0,
        "request": None,          # the command the real system is working on
        "deadline": None,         # when that command times out
        "item": "",
        "real": [],
        "sim": None,              # Unity's last state, passed on to the dashboard
        "log": [],
        "out": [],                # messages to publish, collected while one event is handled
    }

# ---------------------------------------------------------------------- events
def handle(line, topic, payload, now=None):
    now = time.time() if now is None else now
    line["out"] = []
    if topic == UI:
        on_ui(line, payload, now)
    elif topic == SIM_STATE and isinstance(payload, dict):
        on_sim(line, payload, now)
    elif topic == REAL_STATE and isinstance(payload, dict):
        on_real(line, payload)
    line["out"].append((REPORT, view(line)))
    return line["out"]

def check_timeout(line, now):
    if line["waiting_for"] != "real" or line["deadline"] is None or now < line["deadline"]:
        return []
    line["deadline"] = None
    line["real"][line["step"] - 1] = "error"
    line["waiting_for"] = "error"
    report(line, "COORDINATOR", f"No reply from the real system for step {line['step']} within {TIMEOUT_S} s. "
                                + recover_hint(line), "error")
    return [(REPORT, view(line))]

def on_ui(line, cmd, now):
    if cmd in ("target_sim", "target_both"):
        if line["waiting_for"] == "start":
            line["target"] = "both" if cmd == "target_both" else "sim"
            report(line, "USER", "Target: " + target_text(line))
        else:
            report(line, "USER", "Target can only change before Start: press Reset first", "warn")
    elif cmd in ("mode_step", "mode_full"):
        line["mode"] = "full" if cmd == "mode_full" else "step"
        report(line, "USER", "Mode: " + ("full loop" if line["mode"] == "full" else "step-by-step"))
        if line["mode"] == "full" and line["waiting_for"] == "user":
            sim_next(line)
    elif cmd == "start":
        if line["waiting_for"] != "start":
            report(line, "USER", "Start ignored: a run is in progress", "warn")
        else:
            line.update(step=1, real=[""] * len(ACTIONS), item="", request=None, waiting_for="sim")
            send(line, SIM_COMMAND, "mode_step")
            send(line, SIM_COMMAND, "start")
            mode = "full loop" if line["mode"] == "full" else "step-by-step"
            report(line, "USER", f"Start ({target_text(line)}, {mode}): simulation step 1 started")
    elif cmd == "next":
        if line["waiting_for"] == "user":
            sim_next(line)
        else:
            report(line, "USER", "Next ignored: waiting for " + line["waiting_for"], "warn")
    elif cmd == "retry":
        if line["waiting_for"] == "error":
            report(line, "USER", f"Retry step {line['step']} on the real system")
            send_real(line, now)
        else:
            report(line, "USER", "Retry ignored: nothing failed", "warn")
    elif cmd == "empty_shelf":
        if line["waiting_for"] == "start":
            send(line, SIM_COMMAND, "empty_shelf")
            report(line, "USER", "Empty shelf sent to the simulation")
        else:
            report(line, "USER", "Empty shelf only works while the line is idle", "warn")
    elif cmd == "reset":
        if line["waiting_for"] != "start" and line["step"] > RESET_UP_TO_STEP:
            report(line, "USER", f"Reset only works in steps 1 to {RESET_UP_TO_STEP}: finish the cycle first", "warn")
            return
        send(line, SIM_COMMAND, "reset")
        if line["target"] == "both":
            line["seq"] += 1
            send(line, REAL_COMMAND, {"request_id": f"R{line['seq']:04d}", "step": 0, "action": "reset", "item_id": ""})
        line.update(step=0, real=[], item="", request=None, deadline=None, waiting_for="start")
        report(line, "USER", "Reset sent to the simulation" + (" and the real system" if line["target"] == "both" else ""))
    else:
        report(line, "USER", f"Unknown command '{cmd}' ignored", "warn")

def on_sim(line, s, now):
    box = re.search(r"BOX\d+", s.get("state") or "")
    if box:
        line["item"] = box.group(0)
    if s.get("phase") == "offline" and (line["sim"] or {}).get("phase") != "offline":
        report(line, "SIM", "Unity stopped", "warn")
    line["sim"] = {key: s.get(key) for key in
                   ("phase", "step", "steps", "stepSeconds", "state", "shelfStored", "shelfCapacity", "timestamp")}
    step, phase = s.get("step", 0), s.get("phase")
    done_step = {"waiting": step - 1, "done": step, "idle": step if step == len(ACTIONS) else 0}.get(phase, 0)
    if line["waiting_for"] == "sim" and done_step == line["step"]:
        seconds = (s.get("stepSeconds") or [None] * len(ACTIONS))[line["step"] - 1]
        report(line, "SIM", f"Simulation step {line['step']} done ({seconds} s)", "ok")
        if line["target"] == "both":
            send_real(line, now)
        else:
            step_finished(line)

def on_real(line, r):
    if r.get("step") == 0:
        message = f": {r['message']}" if r.get("message") else ""
        report(line, "REAL", f"Reset {r.get('status')}{message}", "error" if r.get("status") == "error" else "info")
        return
    if not line["request"] or r.get("request_id") != line["request"]["request_id"] or line["waiting_for"] != "real":
        print("  real-world reply ignored (not the current request):", r)
        return
    step = line["step"]
    message = f": {r['message']}" if r.get("message") else ""
    status = r.get("status")
    if status == "running":
        line["real"][step - 1] = "running"
        report(line, "REAL", f"Real step {step} running{message}")
    elif status == "done":
        line["deadline"] = None
        line["real"][step - 1] = "done"
        report(line, "REAL", f"Real step {step} done{message}", "ok")
        step_finished(line)
    elif status == "error":
        line["deadline"] = None
        line["real"][step - 1] = "error"
        line["waiting_for"] = "error"
        report(line, "REAL", f"Real step {step} error{message or ': no message'}. " + recover_hint(line), "error")

# ---------------------------------------------------------------------- helpers
def send_real(line, now):
    line["seq"] += 1
    step = line["step"]
    line["request"] = {"request_id": f"R{line['seq']:04d}", "step": step, "action": ACTIONS[step - 1],
                       "item_id": line["item"]}
    send(line, REAL_COMMAND, line["request"])
    line["real"][step - 1] = "sent"
    line["waiting_for"] = "real"
    line["deadline"] = now + TIMEOUT_S
    report(line, "COORDINATOR",
           f"Command to real system: step {step} {line['request']['action']} ({line['request']['request_id']})")

def sim_next(line):
    line["step"] += 1
    line["waiting_for"] = "sim"
    send(line, SIM_COMMAND, "next")
    report(line, "COORDINATOR", f"Simulation step {line['step']} started")

def step_finished(line):
    if line["step"] == len(ACTIONS):
        line["waiting_for"] = "start"
        where = " and the real world" if line["target"] == "both" else ""
        report(line, "COORDINATOR",
               f"Cycle done in the simulation{where}: {line['item']} stored, press Start for the next box", "ok")
    elif line["mode"] == "full":
        sim_next(line)
    else:
        line["waiting_for"] = "user"
        report(line, "COORDINATOR", f"Press Next for step {line['step'] + 1}")

def recover_hint(line):
    return "Press Retry step or Reset" if line["step"] <= RESET_UP_TO_STEP else "Press Retry step"

def target_text(line):
    return "simulation + real world" if line["target"] == "both" else "simulation only"

def send(line, topic, payload):
    line["out"].append((topic, payload))

def report(line, source, text, level="info"):
    line["log"].insert(0, {"time": datetime.now().strftime("%H:%M:%S"), "source": source, "text": text, "level": level})
    del line["log"][40:]
    print(f"[{source}] {text}")

def view(line):
    return {"coordinator": "online", "waitingFor": line["waiting_for"], "mode": line["mode"], "target": line["target"],
            "step": line["step"], "actions": ACTIONS, "real": line["real"], "sim": line["sim"], "log": line["log"]}

# ---------------------------------------------------------------------- MQTT
def main():
    import paho.mqtt.client as mqtt   

    line = new_line()
    lock = threading.Lock()        

    def publish(messages):
        for topic, payload in messages:
            text = payload if isinstance(payload, str) else json.dumps(payload)
            client.publish(topic, text, qos=1, retain=(topic == REPORT))

    def on_connect(client, userdata, flags, reason_code, properties):
        print(f"Connected to {BROKER}:{PORT} ({reason_code})")
        client.subscribe([(UI, 1), (SIM_STATE, 1), (REAL_STATE, 1)])
        with lock:
            publish([(REPORT, view(line))])

    def on_message(client, userdata, msg):
        text = msg.payload.decode("utf-8", errors="replace").strip()
        try:
            payload = text if msg.topic == UI else json.loads(text)
        except json.JSONDecodeError:
            print(f"  not JSON on {msg.topic}: {text}")
            return
        with lock:
            publish(handle(line, msg.topic, payload))

    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
    client.on_connect = on_connect
    client.on_message = on_message
    client.will_set(REPORT, json.dumps({"coordinator": "offline"}), qos=1, retain=True)
    client.connect(BROKER, PORT, keepalive=30)
    client.loop_start()

    try:
        while True:
            time.sleep(1)
            with lock:
                publish(check_timeout(line, time.time()))
    except KeyboardInterrupt:
        pass
    finally:
        client.publish(REPORT, json.dumps({"coordinator": "offline"}), qos=1, retain=True).wait_for_publish(3)
        client.loop_stop()
        client.disconnect()
        print("Coordinator stopped")


if __name__ == "__main__":
    main()
