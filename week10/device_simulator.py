#!/usr/bin/env python3
"""MQTT simulator for the five Week 10 warehouse devices.

It emulates the command/status contract used by Coordinator/twin_coordinator.py:

    dgt_<device>/control  <- JSON command
    dgt_<device>/status   -> JSON status

Every finite command reports a busy state first and its required completion
state afterwards.  This is important because the coordinator deliberately
ignores a completion message until it has seen the device become busy.

RAVAN is simulated at its ESP32 boundary: MQTT is the external device
interface, while ESP32-to-Uno UART remains an internal implementation detail.
The dgt_cart topics therefore mirror RAVAN_ESP32_MQTT_Cycle_INO.ino, including
home, store, pick, cycle, check, and status commands.
"""

from __future__ import annotations

import argparse
import json
import os
import signal
import threading
import time
import uuid
from dataclasses import dataclass
from datetime import datetime
from typing import Any

try:
    import paho.mqtt.client as mqtt
except ImportError as exc:  # friendly error for a fresh classroom PC
    raise SystemExit("paho-mqtt is missing. Install it with:  pip install paho-mqtt") from exc


@dataclass(frozen=True)
class DeviceSpec:
    name: str
    prefix: str
    initial_state: str


SPECS = (
    DeviceSpec("agv", "dgt_agv", "idle"),
    DeviceSpec("arm1", "dgt_rba1", "idle"),
    DeviceSpec("arm2", "dgt_rba2", "idle"),
    DeviceSpec("conveyor", "dgt_conv", "waiting"),
    DeviceSpec("cartesian", "dgt_cart", "ready"),
)


def stamp() -> str:
    return datetime.now().strftime("%H:%M:%S")


class SimulatedDevice:
    def __init__(self, spec: DeviceSpec, broker: str, port: int, time_scale: float, qos: int):
        self.spec = spec
        self.broker = broker
        self.port = port
        self.time_scale = time_scale
        self.qos = qos
        self.control_topic = f"{spec.prefix}/control"
        self.status_topic = f"{spec.prefix}/status"

        self._lock = threading.Lock()
        self._generation = 0
        self._busy = False
        self._stopping = threading.Event()
        self._connected = threading.Event()
        self._agv_speed = 150.0
        self._ravan_homed = False
        self._ravan_pos = "X=0.00,Y=0.00,Z=0.00"
        self._ravan_task = "none"
        self._ravan_phase = "none"
        self._ravan_shelf = 0
        self._ravan_store_shelf = 0
        self._ravan_empty_shelf = 0

        client_id = f"dgt_sim_{spec.name}_{uuid.uuid4().hex[:8]}"
        self.client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id=client_id)
        self.client.on_connect = self._on_connect
        self.client.on_disconnect = self._on_disconnect
        self.client.on_message = self._on_message
        self.client.will_set(
            self.status_topic,
            json.dumps(self._payload("offline"), separators=(",", ":")),
            qos=self.qos,
            retain=True,
        )

    def start(self) -> None:
        self.client.connect(self.broker, self.port, keepalive=30)
        self.client.loop_start()

    def wait_connected(self, timeout: float = 10.0) -> bool:
        return self._connected.wait(timeout)

    def close(self) -> None:
        self._stopping.set()
        with self._lock:
            self._generation += 1
            self._busy = False
        if self.client.is_connected():
            self.publish("offline", ready=False)
            time.sleep(0.05)
            self.client.disconnect()
        self.client.loop_stop()

    def _payload(self, state: str, **extra: Any) -> dict[str, Any]:
        return {
            "state": state,
            "device": self.spec.name,
            "simulated": True,
            "time": stamp(),
            **extra,
        }

    def publish(self, state: str, **extra: Any) -> None:
        payload = self._payload(state, **extra)
        self.client.publish(
            self.status_topic,
            json.dumps(payload, separators=(",", ":")),
            qos=self.qos,
            retain=True,
        )
        detail = ""
        if "progress" in extra:
            detail = f" {extra['progress']:>3}%"
        print(f"{stamp()}  {self.spec.name:<9} -> {state:<12}{detail}")

    def _on_connect(self, client, userdata, flags, reason_code, properties) -> None:
        if reason_code != 0:
            print(f"{self.spec.name}: MQTT connection failed ({reason_code})")
            return
        client.subscribe(self.control_topic, qos=self.qos)
        self._connected.set()
        if self.spec.name == "cartesian":
            self._publish_ravan_state()
        else:
            self.publish(self.spec.initial_state, ready=True)
        print(f"{self.spec.name}: listening on {self.control_topic}")

    def _on_disconnect(self, client, userdata, disconnect_flags, reason_code, properties) -> None:
        self._connected.clear()
        if not self._stopping.is_set():
            print(f"{self.spec.name}: MQTT disconnected ({reason_code}); Paho will reconnect")

    def _on_message(self, client, userdata, message) -> None:
        try:
            command = json.loads(message.payload.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            self.publish("error", msg="command must be a JSON object")
            return
        if not isinstance(command, dict):
            self.publish("error", msg="command must be a JSON object")
            return
        print(f"{stamp()}  {self.spec.name:<9} <- {json.dumps(command, ensure_ascii=False)}")
        self.handle(command)

    def handle(self, command: dict[str, Any]) -> None:
        cmd = str(command.get("cmd", "")).strip().lower()

        # RAVAN's external contract is the ESP32 MQTT gateway.  Keep its
        # behavior separate from the generic simulators so status, auto-home,
        # cycle sequencing, and the unsupported stop command match firmware.
        if self.spec.name == "cartesian":
            self._handle_ravan(command, cmd)
            return

        if cmd == "check":
            with self._lock:
                ready = not self._busy
            self.publish("check", ready=ready)
            return

        if cmd in {"stop", "s"}:
            with self._lock:
                self._generation += 1
                self._busy = False
            self.publish("stopped", ready=True)
            return

        if self.spec.name == "agv" and cmd == "v":
            try:
                speed = float(command.get("value", 0))
            except (TypeError, ValueError):
                speed = 0
            if speed <= 0:
                self.publish("error", msg="AGV speed must be above zero")
            else:
                self._agv_speed = speed
                self.publish("idle", ready=True, speed=self._agv_speed)
            return

        if self.spec.name == "conveyor" and cmd == "start":
            self._start_continuous_conveyor(command)
            return

        profile = self._profile(cmd, command)
        if profile is None:
            self.publish("error", msg=f"unsupported command: {cmd or '<empty>'}")
            return

        busy_state, done_state, seconds = profile
        self._start_finite_job(command, busy_state, done_state, seconds)

    def _handle_ravan(self, command: dict[str, Any], cmd: str) -> None:
        if not cmd:
            self.publish("error", msg="missing or invalid cmd")
            return

        if cmd == "status":
            self._publish_ravan_state()
            return

        if cmd == "stop":
            self.publish("error", msg="UNO firmware cannot stop a blocking motion via UART")
            return

        if cmd == "check":
            with self._lock:
                ready = not self._busy
                homed = self._ravan_homed
                pos = self._ravan_pos
            self.publish(
                "check",
                ready=ready,
                homed=homed,
                pos=pos,
                msg="" if ready else "busy",
                rssi=-45,
            )
            return

        with self._lock:
            busy = self._busy
        if busy:
            self.publish("error", msg="busy, wait for cycle completion")
            return

        if cmd == "home":
            self._start_ravan_job(command, task="home")
            return

        if cmd in {"store", "pick"}:
            shelf = _as_int(command.get("shelf"), 0)
            if not 1 <= shelf <= 6:
                self.publish("error", msg="shelf must be 1..6")
                return
            self._start_ravan_job(command, task=cmd, shelf=shelf)
            return

        if cmd == "cycle":
            store_shelf = _as_int(command.get("store_shelf"), 0)
            empty_shelf = _as_int(command.get("empty_shelf"), 0)
            if not (1 <= store_shelf <= 6 and 1 <= empty_shelf <= 6) or store_shelf == empty_shelf:
                self.publish(
                    "error",
                    msg="cycle needs different store_shelf and empty_shelf, each 1..6",
                )
                return
            self._start_ravan_job(
                command,
                task="cycle",
                shelf=store_shelf,
                store_shelf=store_shelf,
                empty_shelf=empty_shelf,
            )
            return

        self.publish("error", msg="unknown cmd")

    def _publish_ravan_state(self) -> None:
        with self._lock:
            busy = self._busy
            homed = self._ravan_homed
            pos = self._ravan_pos
            task = self._ravan_task
            phase = self._ravan_phase
            shelf = self._ravan_shelf
            store_shelf = self._ravan_store_shelf
            empty_shelf = self._ravan_empty_shelf
        if busy:
            self.publish(
                "moving",
                task=task,
                phase=phase,
                shelf=shelf,
                store_shelf=store_shelf,
                empty_shelf=empty_shelf,
            )
        else:
            self.publish("ready", homed=homed, pos=pos)

    def _start_ravan_job(
        self,
        command: dict[str, Any],
        task: str,
        shelf: int = 0,
        store_shelf: int = 0,
        empty_shelf: int = 0,
    ) -> None:
        generation = self._next_generation()
        with self._lock:
            self._ravan_task = task
            self._ravan_shelf = shelf
            self._ravan_store_shelf = store_shelf
            self._ravan_empty_shelf = empty_shelf
            needs_home = task == "home" or not self._ravan_homed

        def run_phase(phase: str, base_seconds: float, active_shelf: int) -> bool:
            with self._lock:
                self._ravan_phase = phase
                self._ravan_shelf = active_shelf
            self.publish(
                "moving",
                task=task,
                phase=phase,
                shelf=active_shelf,
                store_shelf=store_shelf,
                empty_shelf=empty_shelf,
                command=command,
            )
            return self._sleep_current(generation, max(0.08, base_seconds * self.time_scale))

        def worker() -> None:
            if needs_home:
                if not run_phase("home", 2.2, shelf):
                    return
                with self._lock:
                    self._ravan_homed = True
                    self._ravan_pos = "X=0.00,Y=0.00,Z=0.00"

            if task == "store":
                if not run_phase("store", 4.0, shelf):
                    return
            elif task == "pick":
                if not run_phase("pick", 4.0, shelf):
                    return
            elif task == "cycle":
                if not run_phase("store", 4.0, store_shelf):
                    return
                self.publish(
                    "progress",
                    phase="store_done",
                    store_shelf=store_shelf,
                    next="pick",
                    empty_shelf=empty_shelf,
                )
                if not run_phase("pick", 4.0, empty_shelf):
                    return

            if not self._finish(generation):
                return
            with self._lock:
                self._ravan_phase = "none"
                self._ravan_shelf = 0
            if task == "cycle":
                self.publish(
                    "done",
                    task="cycle",
                    store_shelf=store_shelf,
                    empty_shelf=empty_shelf,
                    ready_point=True,
                )
            else:
                self.publish("done", task=task, shelf=shelf, ready_point=True)

        threading.Thread(target=worker, name="sim-ravan", daemon=True).start()

    def _profile(self, cmd: str, command: dict[str, Any]) -> tuple[str, str, float] | None:
        if self.spec.name == "agv":
            if cmd not in {"f", "b", "l", "r", "cw", "ccw"}:
                return None
            try:
                value = abs(float(command.get("value", 0)))
            except (TypeError, ValueError):
                return None
            if value <= 0:
                return None
            base_seconds = value / (90.0 if cmd in {"cw", "ccw"} else max(self._agv_speed, 1.0))
            return "moving", "done", max(0.35, base_seconds)

        if self.spec.name in {"arm1", "arm2"}:
            if cmd == "move" and isinstance(command.get("value"), list):
                return "moving", "done", 1.4
            if cmd == "joint" and command.get("joint") is not None:
                return "moving", "done", 0.65
            if cmd == "home":
                return "moving", "done", 1.2
            return None

        if self.spec.name == "conveyor":
            if cmd in {"step", "auto"}:
                return "running", "waiting", 2.5
            return None

        if self.spec.name == "cartesian":
            if cmd in {"store", "pick"} and 1 <= _as_int(command.get("shelf"), 0) <= 6:
                return "moving", "done", 4.0
            if cmd == "home":
                return "moving", "done", 2.2
            return None

        return None

    def _next_generation(self) -> int:
        with self._lock:
            self._generation += 1
            self._busy = True
            return self._generation

    def _is_current(self, generation: int) -> bool:
        with self._lock:
            return generation == self._generation and not self._stopping.is_set()

    def _finish(self, generation: int) -> bool:
        with self._lock:
            if generation != self._generation or self._stopping.is_set():
                return False
            self._busy = False
            return True

    def _sleep_current(self, generation: int, seconds: float) -> bool:
        deadline = time.monotonic() + max(0.02, seconds)
        while time.monotonic() < deadline:
            if not self._is_current(generation):
                return False
            time.sleep(min(0.05, deadline - time.monotonic()))
        return self._is_current(generation)

    def _start_finite_job(self, command: dict[str, Any], busy_state: str, done_state: str, base_seconds: float) -> None:
        generation = self._next_generation()
        duration = max(0.08, base_seconds * self.time_scale)

        def worker() -> None:
            self.publish(busy_state, progress=0, command=command)
            for progress in (25, 50, 75):
                if not self._sleep_current(generation, duration / 4):
                    return
                state = "decelerating" if self.spec.name == "conveyor" and progress == 75 else busy_state
                self.publish(state, progress=progress, command=command)
            if not self._sleep_current(generation, duration / 4):
                return
            if self._finish(generation):
                self.publish(done_state, progress=100, command=command, ready=True)

        threading.Thread(target=worker, name=f"sim-{self.spec.name}", daemon=True).start()

    def _start_continuous_conveyor(self, command: dict[str, Any]) -> None:
        generation = self._next_generation()

        def worker() -> None:
            progress = 0
            while self._is_current(generation):
                self.publish("running", progress=progress, command=command, continuous=True)
                progress = (progress + 10) % 100
                if not self._sleep_current(generation, max(0.2, 1.0 * self.time_scale)):
                    return

        threading.Thread(target=worker, name="sim-conveyor-continuous", daemon=True).start()


def _as_int(value: Any, default: int) -> int:
    try:
        return int(value)
    except (TypeError, ValueError):
        return default


def run_self_test() -> None:
    """Validate every command shape required by twin_coordinator without MQTT."""
    expected = {
        "agv": [
            {"cmd": "f", "value": 300},
            {"cmd": "b", "value": 300},
            {"cmd": "cw", "value": 90},
        ],
        "arm1": [
            {"cmd": "move", "value": [50, 85, 125, 75, -1]},
            {"cmd": "joint", "joint": 5, "value": 20},
        ],
        "arm2": [
            {"cmd": "move", "value": [130, 85, 125, 75, -1]},
            {"cmd": "joint", "joint": 5, "value": 90},
        ],
        "conveyor": [{"cmd": "step"}, {"cmd": "auto"}],
        "cartesian": [
            {"cmd": "store", "shelf": 1},
            {"cmd": "pick", "shelf": 6},
            {"cmd": "home"},
        ],
    }
    for spec in SPECS:
        dummy = object.__new__(SimulatedDevice)
        dummy.spec = spec
        dummy._agv_speed = 150.0
        for command in expected[spec.name]:
            result = dummy._profile(command["cmd"], command)
            assert result is not None, (spec.name, command)
            assert result[0] in {"moving", "running"}
            assert result[1] == ("waiting" if spec.name == "conveyor" else "done")
    assert 1 <= _as_int({"store_shelf": 2}["store_shelf"], 0) <= 6
    assert 1 <= _as_int({"empty_shelf": 5}["empty_shelf"], 0) <= 6
    print("Self-test passed: Week 10 commands and RAVAN MQTT command shapes are supported.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Simulate all Week 10 MQTT devices")
    parser.add_argument("--broker", default=os.getenv("DGT_BROKER", "broker.emqx.io"))
    parser.add_argument("--port", type=int, default=int(os.getenv("DGT_PORT", "1883")))
    parser.add_argument(
        "--time-scale",
        type=float,
        default=0.35,
        help="Duration multiplier; 0.35 is fast classroom simulation, 1.0 is slower",
    )
    parser.add_argument("--qos", type=int, choices=(0, 1, 2), default=1)
    parser.add_argument("--self-test", action="store_true", help="Validate command coverage without MQTT")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.self_test:
        run_self_test()
        return
    if args.time_scale <= 0:
        raise SystemExit("--time-scale must be greater than zero")

    stop = threading.Event()

    def request_stop(signum=None, frame=None) -> None:
        stop.set()

    signal.signal(signal.SIGINT, request_stop)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, request_stop)

    devices = [SimulatedDevice(spec, args.broker, args.port, args.time_scale, args.qos) for spec in SPECS]
    print(f"Connecting five simulated devices to {args.broker}:{args.port} (QoS {args.qos})")
    try:
        for device in devices:
            device.start()
        failed = [device.spec.name for device in devices if not device.wait_connected()]
        if failed:
            raise RuntimeError("MQTT connection timed out for: " + ", ".join(failed))
        print("\nAll simulated devices are online. Press Ctrl+C to stop.\n")
        while not stop.wait(0.25):
            pass
    except Exception as exc:
        print(f"Simulator error: {exc}")
        raise
    finally:
        print("\nStopping simulated devices...")
        for device in devices:
            try:
                device.close()
            except Exception as exc:
                print(f"{device.spec.name}: shutdown warning: {exc}")


if __name__ == "__main__":
    main()
