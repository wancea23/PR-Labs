import socket
import sys
import time


def address():
    host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 5000
    return host, port


class Conn:
    def __init__(self):
        self.sock = socket.create_connection(address())
        self.sock.settimeout(0.1)
        self.buf = b""
        self.lines = []
        self.wait_for("WELCOME")

    def send(self, data):
        self.sock.sendall(data if isinstance(data, bytes) else (data + "\n").encode())

    def _pump(self):
        try:
            data = self.sock.recv(4096)
        except socket.timeout:
            return True
        if not data:
            return False
        self.buf += data
        while b"\n" in self.buf:
            line, self.buf = self.buf.split(b"\n", 1)
            self.lines.append(line.decode(errors="replace"))
        return True

    def wait_for(self, prefix, timeout=60):
        end = time.time() + timeout
        while time.time() < end:
            for i, line in enumerate(self.lines):
                if line.startswith(prefix):
                    self.lines = self.lines[i + 1:]
                    return line
            if not self._pump():
                return None
        return None

    def ask(self, command, prefix=""):
        self.send(command)
        return self.wait_for(prefix)

    def read_for(self, seconds):
        end = time.time() + seconds
        while time.time() < end:
            if not self._pump():
                break
        out, self.lines = self.lines, []
        return out

    def close(self):
        self.sock.close()


def stock(conn):
    menu = conn.ask("MENU", "MENU")
    result = {}
    for part in menu[5:].split(" | "):
        name, left = part.split(" (")[0], part.split(": ")[1].split()[0]
        result[name] = int(left)
    return result
