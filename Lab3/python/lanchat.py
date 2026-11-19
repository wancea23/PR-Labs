import base64
import hashlib
import json
import os
import re
import socket
import struct
import sys
import threading
import time

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

PROTOCOL = "lanchat/1"
GROUP = "239.255.42.99"
MAX_TEXT = 500
MAX_DATAGRAM = 4096
HEARTBEAT = 5.0
GONE_AFTER = 15.0
NAME = re.compile(r"^[A-Za-z0-9_-]{1,20}$")


def derive_key(passphrase, port):
    salt = f"{PROTOCOL}:{port}".encode()
    return hashlib.pbkdf2_hmac("sha256", passphrase.encode(), salt, 200_000, 32)


def associated_data(from_id, name, to, seq):
    return f"{from_id}|{name}|{to}|{seq}".encode()


def encode(message):
    return json.dumps({"chat": PROTOCOL, **message}, separators=(",", ":")).encode()


class Peer:
    def __init__(self, name, port, passphrase, show=print):
        self.name = name
        self.port = port
        self.id = os.urandom(8).hex()
        self.show = show
        self.aes = AESGCM(derive_key(passphrase, port))
        self.peers = {}
        self.lock = threading.Lock()
        self.joined = False
        self.closing = False
        self.taken_by = None
        self.seq = 0
        self.pending = {}
        self.seen = set()

        self.group_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        self.group_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if hasattr(socket, "SO_REUSEPORT"):
            self.group_sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
        self.group_sock.bind(("", port))
        membership = struct.pack("4s4s", socket.inet_aton(GROUP), socket.inet_aton("0.0.0.0"))
        self.group_sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, membership)

        self.direct_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
        self.direct_sock.bind(("", 0))
        self.direct_sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
        self.direct_sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_LOOP, 1)

    def send(self, message, to=None):
        try:
            self.direct_sock.sendto(encode(message), to or (GROUP, self.port))
        except OSError:
            pass

    def mine(self, kind, **extra):
        return {"type": kind, "id": self.id, "name": self.name, **extra}

    def join(self):
        for sock in (self.group_sock, self.direct_sock):
            threading.Thread(target=self.receive_loop, args=(sock,), daemon=True).start()
        self.send(self.mine("hello"))
        time.sleep(1)
        if self.taken_by:
            self.show(f"* the name {self.name} is already used by the peer at {self.taken_by}")
            self.close(False)
            return False
        self.joined = True
        self.send(self.mine("join"))
        threading.Thread(target=self.timer_loop, daemon=True).start()
        self.show(f"* you joined the chat as {self.name} (port {self.port}). {self.users()}")
        return True

    def users(self):
        with self.lock:
            lines = [f"Online: {len(self.peers) + 1}", f"  {self.name} (you)"]
            for p in sorted(self.peers.values(), key=lambda p: p["name"]):
                lines.append(f"  {p['name']} at {p['addr'][0]}:{p['addr'][1]}")
        return "\n".join(lines)

    def general(self, text):
        self.send(self.mine("general", text=text))
        self.show(f"[general] {self.name}: {text}")

    def private(self, to, text):
        with self.lock:
            target = next((p for p in self.peers.values() if p["name"] == to), None)
        if target is None:
            self.show(f"* nobody named {to} is online")
            return
        self.seq += 1
        nonce = os.urandom(12)
        data = self.aes.encrypt(nonce, text.encode(), associated_data(self.id, self.name, to, self.seq))
        message = self.mine("private", to=to, seq=self.seq,
                            nonce=base64.b64encode(nonce).decode(), data=base64.b64encode(data).decode())
        self.pending[self.seq] = [message, target["addr"], 1, time.time() + 1, to, text]
        self.send(message, target["addr"])
        self.show(f"[private to {to}] {self.name}: {text}")

    def close(self, bye=True):
        if bye and self.joined:
            self.send(self.mine("bye"))
        self.closing = True
        self.group_sock.close()
        self.direct_sock.close()

    def receive_loop(self, sock):
        while not self.closing:
            try:
                data, addr = sock.recvfrom(65536)
            except OSError:
                return
            try:
                self.handle(data, addr)
            except Exception:
                pass

    def valid(self, data):
        if len(data) > MAX_DATAGRAM:
            return None
        try:
            m = json.loads(data.decode("utf-8"))
        except (UnicodeDecodeError, ValueError):
            return None
        if not isinstance(m, dict) or m.get("chat") != PROTOCOL:
            return None
        if not isinstance(m.get("id"), str) or not re.fullmatch(r"[0-9a-f]{16}", m["id"]):
            return None
        if not isinstance(m.get("name"), str) or not NAME.match(m["name"]):
            return None
        if m.get("type") == "general" and not (isinstance(m.get("text"), str) and 0 < len(m["text"]) <= MAX_TEXT):
            return None
        return m

    def handle(self, data, addr):
        m = self.valid(data)
        if m is None or m["id"] == self.id:
            return
        kind = m.get("type")
        if kind == "hello":
            if m["name"] == self.name:
                if self.joined or self.id < m["id"]:
                    self.send(self.mine("taken"), addr)
            elif self.joined:
                self.send(self.mine("here"), addr)
        elif kind == "taken":
            if m["name"] == self.name and not self.joined:
                self.taken_by = f"{addr[0]}:{addr[1]}"
        elif kind in ("here", "join", "alive"):
            if not self.joined and kind != "here":
                return
            with self.lock:
                new = m["id"] not in self.peers
                self.peers[m["id"]] = {"name": m["name"], "addr": addr, "seen": time.time()}
            if new and kind != "here" and self.joined:
                self.show(f"* {m['name']} joined the chat")
        elif kind == "bye":
            with self.lock:
                gone = self.peers.pop(m["id"], None)
            if gone:
                self.show(f"* {m['name']} left the chat")
        elif kind == "general" and self.joined:
            self.show(f"[general] {m['name']}: {m['text']}")
        elif kind == "private" and m.get("to") == self.name and self.joined:
            try:
                seq = int(m["seq"])
                text = self.aes.decrypt(base64.b64decode(m["nonce"]), base64.b64decode(m["data"]),
                                        associated_data(m["id"], m["name"], self.name, seq)).decode()
            except (InvalidTag, KeyError, ValueError, TypeError):
                return
            self.send(self.mine("ack", seq=seq), addr)
            if (m["id"], seq) not in self.seen:
                self.seen.add((m["id"], seq))
                self.show(f"[private] {m['name']}: {text}")
        elif kind == "ack":
            self.pending.pop(m.get("seq"), None)

    def timer_loop(self):
        next_beat = time.time() + HEARTBEAT
        while not self.closing:
            time.sleep(0.2)
            now = time.time()
            if now >= next_beat:
                self.send(self.mine("alive"))
                next_beat = now + HEARTBEAT
            with self.lock:
                gone = [i for i, p in self.peers.items() if now - p["seen"] > GONE_AFTER]
                names = [self.peers.pop(i)["name"] for i in gone]
            for name in names:
                self.show(f"* {name} left the chat (not heard from for 15 s)")
            for seq, p in list(self.pending.items()):
                if now < p[3]:
                    continue
                if p[2] >= 3:
                    self.pending.pop(seq, None)
                    self.show(f"* {p[4]} did not confirm your private message \"{p[5][:30]}\"")
                else:
                    p[2] += 1
                    p[3] = now + 1
                    self.send(p[0], p[1])


def main():
    if len(sys.argv) != 4 or not NAME.match(sys.argv[1]) or not sys.argv[2].isdigit():
        print("usage: python3 lanchat.py <name> <port> <passphrase>")
        return 1
    peer = Peer(sys.argv[1], int(sys.argv[2]), sys.argv[3])
    if not peer.join():
        return 2
    try:
        for line in sys.stdin:
            text = line.strip()
            if not text:
                continue
            if text == "/quit":
                break
            if text == "/users":
                print(peer.users())
            elif text.startswith("/msg "):
                parts = text.split(" ", 2)
                if len(parts) < 3:
                    print("* usage: /msg name text")
                elif len(parts[2]) <= MAX_TEXT:
                    peer.private(parts[1], parts[2])
            elif text.startswith("/"):
                print(f"* unknown command {text.split()[0]}")
            elif len(text) <= MAX_TEXT:
                peer.general(text)
    except KeyboardInterrupt:
        pass
    peer.close()
    print("* you left the chat")
    return 0


if __name__ == "__main__":
    sys.exit(main())
