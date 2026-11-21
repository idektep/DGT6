"""
Digital twin coordinator: runs the warehouse workflow on the real devices over MQTT
and reports live status + work status to the Node-RED dashboard.

The workflow uses the values locked on the dashboard's Manual panels (AGV route, arm poses,
gripper angles). They are saved in sequence.json next to this file, so they survive a restart.

Topics (all on broker.emqx.io:1883, JSON)
  dgt/twin/ui       <- Node-RED: {"cmd": ..., "value": ...}
                       start | next | stop | mode (step|full) | shelf (1-6) | check (device|all) | lock ({...})
  dgt/twin/report   -> Node-RED: devices (live status) + work (workflow) + sequence (locked values)  (retained)
  <prefix>/control  -> device command, e.g. dgt_rba2/control {"cmd":"move","value":[50,85,125,75,-1]}
  <prefix>/status   <- device reply,   e.g. {"state":"moving"} / {"state":"done"} / {"state":"check","ready":true}

Cartesian robot API (firmware still to be written; same pattern as the other devices)
  dgt_cart/control:  {"cmd":"store","shelf":1..6}  {"cmd":"home"}  {"cmd":"stop"}  {"cmd":"check"}
  dgt_cart/status:   {"state":"moving"} ... {"state":"done"} | {"state":"error","msg":"..."}
                     {"state":"check","ready":true|false}   last will {"state":"offline"}
"""
import json
import threading
import time
from datetime import datetime
from pathlib import Path

BROKER, PORT = "broker.emqx.io", 1883
UI = "dgt/twin/ui"
REPORT = "dgt/twin/report"
SEQUENCE_FILE = Path(__file__).resolve().parent / "sequence.json"

# ---------------------------------------------------------------------- devices
DEVICES = {               # name -> MQTT topic prefix
    "agv": "dgt_agv",
    "arm1": "dgt_rba1",   # picks from the AGV, places on the Cartesian robot
    "arm2": "dgt_rba2",   # picks from the conveyor, places on the AGV
    "conveyor": "dgt_conv",
    "cartesian": "dgt_cart",
}
DONE_STATE = {"agv": "done", "arm1": "done", "arm2": "done", "conveyor": "waiting", "cartesian": "done"}
BUSY_STATES = {"moving", "running", "decelerating"}
STOPPED_STATES = {"stopped", "manual_stop"}      # stopped by someone else, or a conveyor stall
STOP_COMMAND = {"agv": {"cmd": "s"}}            # every other device uses {"cmd": "stop"}

# ---------------------------------------------------------------------- workflow
STEPS = ["conveyor_to_arm2", "arm2_pick", "arm2_place_on_agv", "agv_to_arm1",
         "arm1_pick", "arm1_place_on_cartesian", "cartesian_store", "agv_cartesian_home"]
GRIP_WAIT_S = 2.0
KEEP = -1                                        # arm "move": keep J5 (gripper) where it is
AGV_MOVES = {"f": "b", "b": "f", "l": "r", "r": "l", "cw": "ccw", "ccw": "cw"}   # move -> its reverse
DEFAULT_SEQUENCE = {
    "agv": {"speed": 150, "route": [{"cmd": "f", "value": 500}]},
    "arm1": {"pick": [50, 85, 125, 75], "place": [130, 85, 125, 75], "grip_on": 20, "grip_off": 90},
    "arm2": {"pick": [50, 85, 125, 75], "place": [130, 85, 125, 75], "grip_on": 20, "grip_off": 90},
}
COMMAND_TIMEOUT_S = 60
DEVICE_TIMEOUT_S = {"cartesian": 180}           # the Uno may home first: home + store takes over a minute
CHECK_TIMEOUT_S = 5
SHELVES = range(1, 7)

# Each step is a list of items, run one after another:
#   {"to": device, "cmd": {...}}                  send, then wait for busy -> done
#   {"to": device, "cmd": {...}, "nowait": True}  send and go on (e.g. AGV speed)
#   {"wait": seconds}
def step_items(step, seq, shelf):
    def gripper(name, deg):
        return {"to": name, "cmd": {"cmd": "joint", "joint": 5, "value": deg}}

    def arm(name, action):
        a = seq[name]
        pose = a[action]
        # pick opens the gripper first: it may still be closed (power-up HOME closes it)
        first = [gripper(name, a["grip_off"])] if action == "pick" else []
        return first + [{"to": name, "cmd": {"cmd": "move", "value": list(pose) + [KEEP]}},
                        gripper(name, a["grip_on"] if action == "pick" else a["grip_off"]),
                        {"wait": GRIP_WAIT_S}]

    agv = seq["agv"]
    speed = [{"to": "agv", "cmd": {"cmd": "v", "value": agv["speed"]}, "nowait": True}]
    home = [{"cmd": AGV_MOVES[m["cmd"]], "value": m["value"]} for m in reversed(agv["route"])]
    return {
        "conveyor_to_arm2": [{"to": "conveyor", "cmd": {"cmd": "step"}}],
        "arm2_pick": arm("arm2", "pick"),
        "arm2_place_on_agv": arm("arm2", "place"),
        "agv_to_arm1": speed + [{"to": "agv", "cmd": m} for m in agv["route"]],
        "arm1_pick": arm("arm1", "pick"),
        "arm1_place_on_cartesian": arm("arm1", "place"),
        "cartesian_store": [{"to": "cartesian", "cmd": {"cmd": "store", "shelf": shelf}}],
        "agv_cartesian_home": [{"to": "cartesian", "cmd": {"cmd": "home"}}]
                              + speed + [{"to": "agv", "cmd": m} for m in home],
    }[step]

# ---------------------------------------------------------------------- locked values
def load_sequence():
    seq = json.loads(json.dumps(DEFAULT_SEQUENCE))
    if SEQUENCE_FILE.exists():
        saved = json.loads(SEQUENCE_FILE.read_text(encoding="utf-8"))
        for device in seq:
            seq[device].update(saved.get(device, {}))
    return seq

def save_sequence(seq):
    SEQUENCE_FILE.write_text(json.dumps(seq, indent=2) + "\n", encoding="utf-8")

def valid_lock(value):
    """Returns (device, values) for a valid lock request, or (None, reason)."""
    device = value.get("device") if isinstance(value, dict) else None

    def numbers(xs, n):
        return isinstance(xs, list) and len(xs) == n and all(isinstance(x, (int, float)) and 0 <= x <= 180 for x in xs)

    if device in ("arm1", "arm2"):
        ok = numbers(value.get("pick"), 4) and numbers(value.get("place"), 4) \
            and numbers([value.get("grip_on"), value.get("grip_off")], 2)
        return (device, {k: value[k] for k in ("pick", "place", "grip_on", "grip_off")}) if ok \
            else (None, "arm lock needs pick/place J1-J4 and gripper angles, 0-180")
    if device == "agv":
        route, speed = value.get("route"), value.get("speed")
        ok = isinstance(route, list) and all(isinstance(m, dict) and m.get("cmd") in AGV_MOVES
                                             and isinstance(m.get("value"), (int, float)) and m["value"] > 0
                                             for m in route) \
            and isinstance(speed, (int, float)) and speed > 0
        return (device, {"route": [{"cmd": m["cmd"], "value": m["value"]} for m in route], "speed": speed}) if ok \
            else (None, "AGV lock needs recorded moves and a speed above 0")
    return None, "unknown device"

# ---------------------------------------------------------------------- state
def new_twin(sequence=None):
    return {
        "waiting_for": "start",   # start | device | user | error | stopped
        "mode": "step",           # step | full
        "shelf": 1,
        "step": 0,                # 1-based, 0 = idle
        "cmd_index": 0,           # which item of the current step
        "steps": [""] * len(STEPS),
        "busy_seen": False,       # the device has reported a busy state for the current command
        "deadline": None,         # command timeout
        "wait_until": None,       # end of a {"wait"} item
        "cycles": 0,
        "sequence": sequence or json.loads(json.dumps(DEFAULT_SEQUENCE)),
        "devices": {name: {"online": None, "state": "", "seen": "", "last": {},
                           "check": "", "check_time": "", "check_deadline": None} for name in DEVICES},
        "message": {"time": "", "text": "Press Start for a cycle", "level": "info"},
        "out": [],
    }

# ---------------------------------------------------------------------- events
def handle(twin, topic, payload, now=None):
    now = time.time() if now is None else now
    twin["out"] = []
    if topic == UI and isinstance(payload, dict):
        on_ui(twin, payload.get("cmd"), payload.get("value"), now)
    else:
        name = device_of(topic)
        if name and isinstance(payload, dict):
            on_status(twin, name, payload, now)
    twin["out"].append((REPORT, view(twin)))
    return twin["out"]

def check_timeouts(twin, now):
    twin["out"] = []
    changed = False
    if twin["wait_until"] and now >= twin["wait_until"]:
        twin["wait_until"] = None
        item_done(twin, now)
        changed = True
    if twin["waiting_for"] == "device" and twin["deadline"] and now >= twin["deadline"]:
        device = current_item(twin)["to"]
        # it may still be running (e.g. a belt with no box coming): stop it
        send(twin, f"{DEVICES[device]}/control", STOP_COMMAND.get(device, {"cmd": "stop"}))
        fail(twin, device.upper(), f"no reply within {DEVICE_TIMEOUT_S.get(device, COMMAND_TIMEOUT_S)} s, "
                                   "stop sent")
        changed = True
    for name, dev in twin["devices"].items():
        if dev["check_deadline"] and now >= dev["check_deadline"]:
            dev.update(check="no reply", check_time=clock(), check_deadline=None, online=False)
            report(twin, name.upper(), "Check: no reply (offline or not connected)", "error")
            changed = True
    if changed:
        twin["out"].append((REPORT, view(twin)))
    return twin["out"]

def on_ui(twin, cmd, value, now):
    waiting = twin["waiting_for"]
    if cmd == "mode" and value in ("step", "full"):
        twin["mode"] = value
        report(twin, "USER", "Mode: " + ("full loop" if value == "full" else "step-by-step"))
        if value == "full" and waiting == "user":
            next_step(twin, now)
    elif cmd == "shelf":
        if waiting == "device":
            report(twin, "USER", "Shelf can't change while a device is working", "warn")
        elif int_or_none(value) in SHELVES:
            twin["shelf"] = int(value)
            report(twin, "USER", f"Shelf {twin['shelf']} selected")
    elif cmd == "start":
        if waiting in ("device", "user"):
            report(twin, "USER", "Start ignored: a cycle is in progress (STOP ALL first)", "warn")
        else:   # idle, or after an error / stop: a new cycle from step 1
            twin.update(step=0, steps=[""] * len(STEPS))
            report(twin, "USER", f"Start cycle: shelf {twin['shelf']}, "
                                 + ("full loop" if twin["mode"] == "full" else "step-by-step"))
            next_step(twin, now)
    elif cmd == "next":
        if waiting == "user":
            next_step(twin, now)
        else:
            report(twin, "USER", "Next ignored: waiting for " + waiting, "warn")
    elif cmd == "stop":
        for name in DEVICES:
            send(twin, f"{DEVICES[name]}/control", STOP_COMMAND.get(name, {"cmd": "stop"}))
        if twin["step"] and waiting in ("device", "user", "error"):
            twin["steps"][twin["step"] - 1] = "stopped"
        twin.update(waiting_for="stopped", deadline=None, wait_until=None)
        report(twin, "USER", "STOP sent to all devices. Press Start for a new cycle", "error")
    elif cmd == "lock":
        if waiting == "device":
            report(twin, "USER", "Lock ignored: a device is working", "warn")
            return
        device, values = valid_lock(value)
        if not device:
            report(twin, "USER", "Lock ignored: " + values, "warn")
            return
        twin["sequence"][device] = values
        save_sequence(twin["sequence"])
        report(twin, "USER", f"{device} values locked for the workflow", "ok")
    elif cmd == "check":
        names = list(DEVICES) if value == "all" else [value] if value in DEVICES else []
        for name in names:
            send(twin, f"{DEVICES[name]}/control", {"cmd": "check"})
            twin["devices"][name].update(check="pending", check_deadline=now + CHECK_TIMEOUT_S)
        if names:
            report(twin, "USER", "Check status: " + ", ".join(names))
    else:
        report(twin, "USER", f"Unknown command '{cmd}' ignored", "warn")

def on_status(twin, name, s, now):
    dev = twin["devices"][name]
    state = s.get("state", "")
    if state == "offline" and dev["online"]:
        report(twin, name.upper(), "went offline", "error")
    dev.update(online=state != "offline", state=state, seen=clock(), last=s)

    if state == "check":
        ready = bool(s.get("ready"))
        asked = dev["check"] == "pending"   # not an old retained reply from before this run
        dev.update(check="ready" if ready else "not ready", check_time=clock(), check_deadline=None)
        if asked:
            report(twin, name.upper(), "Check: ready" if ready else "Check: NOT ready (busy or fault)",
                   "ok" if ready else "warn")
        return

    item = current_item(twin) if twin["waiting_for"] == "device" else None
    if not item or item.get("to") != name:
        return
    if state == "error" or state == "offline":
        fail(twin, name.upper(), s.get("msg") or state)
    elif twin["busy_seen"] and (s.get("fault") is True or state in STOPPED_STATES):
        fail(twin, name.upper(), "fault: stalled or encoder not counting" if s.get("fault") else state)
    elif state in BUSY_STATES:
        twin["busy_seen"] = True
    elif state == DONE_STATE[name] and twin["busy_seen"]:
        item_done(twin, now)

# ---------------------------------------------------------------------- workflow helpers
def items_of(twin):
    return step_items(STEPS[twin["step"] - 1], twin["sequence"], twin["shelf"])

def current_item(twin):
    return items_of(twin)[twin["cmd_index"]]

def next_step(twin, now):
    twin["step"] += 1
    twin["cmd_index"] = 0
    twin["steps"][twin["step"] - 1] = "running"
    run_items(twin, now)

def run_items(twin, now):
    """Runs items from cmd_index until one has to wait, or the step is finished."""
    items = items_of(twin)
    while twin["cmd_index"] < len(items):
        item = items[twin["cmd_index"]]
        if "wait" in item:
            twin.update(waiting_for="device", wait_until=now + item["wait"], deadline=None)
            report(twin, "COORD", f"Step {twin['step']}: wait {item['wait']:g} s")
            return
        send(twin, f"{DEVICES[item['to']]}/control", item["cmd"])
        report(twin, "COORD", f"Step {twin['step']} {STEPS[twin['step'] - 1]}: "
                              f"{item['to']} <- {json.dumps(item['cmd'])}")
        if not item.get("nowait"):
            timeout = DEVICE_TIMEOUT_S.get(item["to"], COMMAND_TIMEOUT_S)
            twin.update(waiting_for="device", busy_seen=False, deadline=now + timeout)
            return
        twin["cmd_index"] += 1
    step_done(twin, now)

def item_done(twin, now):
    twin["deadline"] = None
    twin["cmd_index"] += 1
    run_items(twin, now)

def step_done(twin, now):
    step = twin["step"]
    twin["steps"][step - 1] = "done"
    report(twin, "COORD", f"Step {step} {STEPS[step - 1]} done", "ok")
    if step == len(STEPS):
        twin["cycles"] += 1
        twin["waiting_for"] = "start"
        report(twin, "COORD", f"Cycle done: box stored on shelf {twin['shelf']}. Press Start for the next box", "ok")
    elif twin["mode"] == "full":
        next_step(twin, now)
    else:
        twin["waiting_for"] = "user"
        report(twin, "COORD", f"Step {step} done. Press Next for step {step + 1}")

def fail(twin, source, why):
    twin["steps"][twin["step"] - 1] = "error"
    twin.update(waiting_for="error", deadline=None, wait_until=None)
    report(twin, source, f"Step {twin['step']} failed: {why}. Press Start to run the cycle again from step 1",
           "error")

# ---------------------------------------------------------------------- small helpers
def device_of(topic):
    for name, prefix in DEVICES.items():
        if topic == f"{prefix}/status":
            return name
    return None

def int_or_none(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None

def clock():
    return datetime.now().strftime("%H:%M:%S")

def send(twin, topic, payload):
    twin["out"].append((topic, payload))

def report(twin, source, text, level="info"):
    twin["message"] = {"time": clock(), "text": f"{source}: {text}", "level": level}
    print(f"[{source}] {text}")

def view(twin):
    item = None   # what the current step is doing: {"to": device, "cmd": {...}} or {"wait": seconds}
    if twin["waiting_for"] in ("device", "error") and twin["step"]:
        item = {k: v for k, v in current_item(twin).items() if k != "nowait"}
    return {
        "coordinator": "online",
        "devices": {name: {**{k: v for k, v in dev.items() if k != "check_deadline"}, "prefix": DEVICES[name]}
                    for name, dev in twin["devices"].items()},
        "work": {"waitingFor": twin["waiting_for"], "mode": twin["mode"], "shelf": twin["shelf"],
                 "step": twin["step"], "cycles": twin["cycles"], "item": item, "message": twin["message"],
                 "steps": [{"name": name, "status": status} for name, status in zip(STEPS, twin["steps"])]},
        "sequence": twin["sequence"],
    }

# ---------------------------------------------------------------------- MQTT
def main():
    import paho.mqtt.client as mqtt

    twin = new_twin(load_sequence())
    print(f"Locked values: {SEQUENCE_FILE}")
    lock = threading.Lock()

    def publish(messages):
        for topic, payload in messages:
            client.publish(topic, json.dumps(payload), qos=1, retain=(topic == REPORT))

    def on_connect(client, userdata, flags, reason_code, properties):
        print(f"Connected to {BROKER}:{PORT} ({reason_code})")
        client.subscribe([(UI, 1)] + [(f"{prefix}/status", 1) for prefix in DEVICES.values()])
        with lock:
            publish([(REPORT, view(twin))])

    def on_message(client, userdata, msg):
        text = msg.payload.decode("utf-8", errors="replace").strip()
        try:
            payload = json.loads(text)
        except json.JSONDecodeError:
            print(f"  not JSON on {msg.topic}: {text}")
            return
        with lock:
            publish(handle(twin, msg.topic, payload))

    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
    client.on_connect = on_connect
    client.on_message = on_message
    client.will_set(REPORT, json.dumps({"coordinator": "offline"}), qos=1, retain=True)
    client.connect(BROKER, PORT, keepalive=30)
    client.loop_start()

    try:
        while True:
            time.sleep(0.2)
            with lock:
                publish(check_timeouts(twin, time.time()))
    except KeyboardInterrupt:
        pass
    finally:
        client.publish(REPORT, json.dumps({"coordinator": "offline"}), qos=1, retain=True).wait_for_publish(3)
        client.loop_stop()
        client.disconnect()
        print("Twin coordinator stopped")


if __name__ == "__main__":
    main()
