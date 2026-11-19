import json
import os
import socket
import sys
import time

GROUP = "239.255.42.99"
port = int(sys.argv[1]) if len(sys.argv) > 1 else 5050
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)


def send(label, data):
    sock.sendto(data, (GROUP, port))
    print(f"sent {len(data):6} bytes: {label}")
    time.sleep(0.2)


def message(**fields):
    return json.dumps({"chat": "lanchat/1", "id": "0123456789abcdef", "name": "Tester", **fields}).encode()


send("random bytes", os.urandom(300))
send("text that is not JSON", b"hello, is anybody there?")
send("JSON of another program", json.dumps({"cmd": "ping", "seq": 1}).encode())
send("unknown message type", message(type="dance"))
send("general message without text", message(type="general"))
send("bad name", json.dumps({"chat": "lanchat/1", "type": "general", "id": "0123456789abcdef",
                            "name": "Tester with spaces", "text": "hi"}).encode())
send("general message of 600 characters", message(type="general", text="x" * 600))
send("datagram of 5000 bytes", message(type="general", text="y" * 4900))
send("datagram of 60000 bytes", b"z" * 60000)
send("a valid general message", message(type="general", text="this one is valid"))
