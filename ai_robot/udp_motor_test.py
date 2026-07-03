#!/usr/bin/env python3
"""
udp_motor_test.py  —  Isolation test for the Robot-Soccer ESP32.

WHY: brain_runner sends the RIGHT commands, but "policy looks stuck / spins"
can mean either (a) the robot never physically executes, or (b) it executes the
WRONG way. This tool removes the brain, the model, the camera and the coordinate
math from the loop so you test ONE thing: does a raw command byte move the robot,
and in which direction — and does the ESP32's status beacon come back.

RUN brain_runner.py STOPPED (both bind UDP 4214; only one can at a time).

Usage:
    python udp_motor_test.py                 # uses 192.168.8.149
    python udp_motor_test.py 192.168.8.152   # test the other robot

Keys (press Enter after each):
    f  forward      l  rotate-left(CCW)     r  rotate-right(CW)
    b  backward     s  stop
    w  auto-wiggle  (F, L, R, B each ~1s — good for a quick "is it alive")
    q  quit

Each key drives the robot for BURST_SECONDS by re-sending every 100 ms (the ESP32
stops itself 400 ms after the last packet — its safety watchdog — so a single
packet would look like nothing happened). A line prints every second telling you
whether an "OK" beacon has arrived from the robot on port 4214.
"""
import socket, sys, threading, time

ROBOT_IP    = sys.argv[1] if len(sys.argv) > 1 else "192.168.8.149"
CMD_PORT    = 4210          # must match config.ESP32_CMD_PORT
STATUS_PORT = 4214          # must match config.ESP32_STATUS_PORT
BURST_SECONDS = 1.2         # how long one key-press drives the motors
RESEND_HZ   = 10

_last_beacon = [0.0]
_beacon_count = [0]

def beacon_listener():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.bind(("0.0.0.0", STATUS_PORT))
    except OSError as e:
        print(f"[beacon] cannot bind {STATUS_PORT}: {e}\n"
              f"         -> is brain_runner.py still running? Stop it and retry.")
        return
    s.settimeout(1.0)
    print(f"[beacon] listening on udp/{STATUS_PORT} for 'OK' from the robot")
    while True:
        try:
            data, addr = s.recvfrom(32)
            _last_beacon[0] = time.time()
            _beacon_count[0] += 1
            print(f"[beacon] <- {data!r} from {addr[0]}  (total {_beacon_count[0]})")
        except socket.timeout:
            pass

def send_burst(sock, cmd: bytes, seconds: float):
    end = time.time() + seconds
    period = 1.0 / RESEND_HZ
    while time.time() < end:
        sock.sendto(cmd, (ROBOT_IP, CMD_PORT))
        time.sleep(period)
    sock.sendto(b"S", (ROBOT_IP, CMD_PORT))   # stop at the end

def main():
    threading.Thread(target=beacon_listener, daemon=True).start()
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"[cmd] target robot {ROBOT_IP}:{CMD_PORT}")
    print(__doc__)

    keymap = {"f": b"F", "l": b"L", "r": b"R", "b": b"B", "s": b"S"}
    while True:
        try:
            k = input("cmd> ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            break
        age = time.time() - _last_beacon[0] if _last_beacon[0] else 1e9
        alive = "ALIVE" if age < 2.0 else "NO BEACON YET"
        print(f"     [robot link: {alive}]")
        if k == "q":
            break
        elif k == "w":
            for label, c in [("FWD", b"F"), ("LEFT", b"L"), ("RIGHT", b"R"), ("BACK", b"B")]:
                print(f"     wiggle: {label}")
                send_burst(sock, c, 1.0)
                time.sleep(0.3)
        elif k in keymap:
            print(f"     driving '{keymap[k].decode()}' for {BURST_SECONDS}s ...")
            send_burst(sock, keymap[k], BURST_SECONDS)
        else:
            print("     unknown key")
    sock.sendto(b"S", (ROBOT_IP, CMD_PORT))
    print("stopped. bye.")

if __name__ == "__main__":
    main()
