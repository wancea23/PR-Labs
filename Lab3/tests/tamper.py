import base64
import json
import os
import socket
import sys
import time

sys.path.insert(0, __file__.rsplit("/", 2)[0] + "/python")
import lanchat

port = int(sys.argv[1]) if len(sys.argv) > 1 else 5050
passphrase = sys.argv[2] if len(sys.argv) > 2 else "secret"
target = sys.argv[3] if len(sys.argv) > 3 else "Ion"

found = {}
probe = lanchat.Peer("Tamper", port, passphrase, show=lambda s: None)
probe.join()
time.sleep(0.5)
with probe.lock:
    for p in probe.peers.values():
        found[p["name"]] = p["addr"]
probe.close()
addr = found[target]
print(f"{target} is at {addr[0]}:{addr[1]}")

aes = lanchat.AESGCM(lanchat.derive_key(passphrase, port))
sender_id = os.urandom(8).hex()
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)


def private(name, text, seq):
    nonce = os.urandom(12)
    data = aes.encrypt(nonce, text.encode(), lanchat.associated_data(sender_id, name, target, seq))
    return {"chat": "lanchat/1", "type": "private", "id": sender_id, "name": name, "to": target, "seq": seq,
            "nonce": base64.b64encode(nonce).decode(), "data": base64.b64encode(data).decode()}


def send(label, m):
    sock.sendto(json.dumps(m).encode(), addr)
    print(f"sent: {label}")
    time.sleep(0.4)


original = private("Mallory", "the meeting is at five", 1)
send("a correct private message from Mallory", original)

changed = dict(original)
raw = bytearray(base64.b64decode(changed["data"]))
raw[4] ^= 0x01
changed["data"] = base64.b64encode(bytes(raw)).decode()
changed["seq"] = 2
send("the same message with one bit of the ciphertext changed", changed)

renamed = dict(original)
renamed["name"] = "Ana"
send("the same message with the author changed to Ana", renamed)

send("the first message again (a replay)", original)
