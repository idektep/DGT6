import json
import paho.mqtt.client as mqtt

BROKER = "broker.emqx.io"
PORT = 1883
# TOPIC = "dgt_agv/control"  
TOPIC = "dgt_conv/control"  
client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)

client.connect(BROKER, PORT, 60)
client.loop_start()

# payload = {"cmd": "cw", "value": 340}
# payload = {"cmd":"speed","value":150}
payload = {"cmd": "auto"}

info = client.publish(
    TOPIC,
    json.dumps(payload, separators=(",", ":")),
    qos=1
)

info.wait_for_publish()

print("Published:", json.dumps(payload))

client.disconnect()
client.loop_stop()