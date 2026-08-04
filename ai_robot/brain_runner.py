"""
brain_runner.py — Robot Soccer Project  (REDESIGN runtime — pairs with new models)
==================================================================================

PAIRS WITH the redesigned RobotAgent.cs. Observation contract:

  Easy   (13): robot.xz, heading.xz, ball.rel.xz, ball.dist, BALL.VEL.xz,
               goal.rel.xz, goal.dist, in_control
  Medium (19): + opp.rel.xz, opp.heading.xz, opp.dist, opp_in_control
  Hard   (24): + owngoal.rel.xz, owngoal.dist, role_flag, ball->owngoal.dist

KEY DIFFERENCES vs the previous runtime
  • Possession latch REMOVED. "control" is continuous: the ball is within the
    control distance AND in front of the robot (same rule as RobotAgent.cs).
  • Ball VELOCITY is now an observation — computed from warped-position deltas,
    converted px/s -> m/s via the arena scale, divided by VEL_SCALE.
  • Scoring is BALL-based for BOTH sides.
  • Single ball (real soccer). The nearest detected ball is "the" ball.
  • Hard role_flag is computed dynamically.

WHAT CHANGED IN THIS REVISION
  • Blue highlight ring drawn around every detected ball (config.SHOW_BALL_HIGHLIGHT).
  • Heading arrows drawn per marker (config.DEBUG_ARUCO_HEADING).
  • Turn diagnostic: robot 1 → ball line + facing dot + recommended turn drawn on
    the display (config.DEBUG_TURN_DIAGNOSTIC) so a left/right inversion is visible.
  • config.MIRROR_X mirrors the X axis of EVERY observation consistently — the
    single correct lever for a reflected world frame (see config Section 6).
  • Robust display send: JPEG is downscaled if it would exceed one UDP datagram.
"""

import sys
import cv2
import numpy as np
import threading
import time
import math
import socket
import json
import os

import onnxruntime as ort

import config as C

# Many status lines below use em-dashes / box-drawing characters. Windows
# consoles default to a legacy codepage (e.g. cp1252) that can't encode them,
# which crashes every print() call rather than just mangling the glyph.
# Force UTF-8 with a lossy fallback so the runtime never dies on cosmetics.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# ── Redesign constants (config overrides if present) ─────────────────────────
ARENA_HALF_M       = getattr(C, "ARENA_HALF_M", 0.75)
VEL_SCALE          = getattr(C, "VEL_SCALE", 1.5)
ROBOT_VEL_SCALE    = getattr(C, "ROBOT_VEL_SCALE", 0.60)
YAW_RATE_SCALE     = getattr(C, "YAW_RATE_SCALE", 3.2)
CONTROL_DIST_M     = getattr(C, "CONTROL_DIST_M", 0.18)
CONTROL_FACING_DOT = getattr(C, "CONTROL_FACING_DOT", 0.30)
GOAL_RADIUS_M      = getattr(C, "GOAL_RADIUS_M", 0.20)
SCORE_COOLDOWN_S   = 3.0
GOAL_PAUSE_S       = getattr(C, "GOAL_PAUSE_S", 6.0)   # robot stops this long after a goal for repositioning

# Authoritative obs widths per difficulty (matches RobotAgent.cs). 2v2 is a
# fixed 26-float team contract regardless of difficulty — see EXPECTED_OBS's
# call site in brain_thread_fn and build_team_observations() below.
EXPECTED_OBS = {"easy": 13, "medium": 19, "hard": 24}
TEAM_OBS_SIZE = 26

# =====================================================================
# GLOBAL MATCH STATE
# =====================================================================

match_difficulty = "easy"
match_mode       = "1v1"
match_lock       = threading.Lock()

robot_started  = False
started_lock   = threading.Lock()
stop_requested = False

# =====================================================================
# UDP SOCKETS
# =====================================================================

_unity_event_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

def send_unity_event(event: str):
    try:
        _unity_event_sock.sendto(json.dumps({"event": event}).encode("utf-8"),
                                 (C.UNITY_IP, C.UNITY_EVENT_PORT))
    except Exception as e:
        print(f"[Unity] Send error: {e}")

_esp32_socks: dict = {}
_esp32_addrs: dict = {}
esp32_last_seen: dict = {}
esp32_status_lock = threading.Lock()
_ip_to_robot = {}

def _init_esp32_sockets():
    global _esp32_socks, _esp32_addrs, _ip_to_robot
    for s in _esp32_socks.values():
        try: s.close()
        except Exception: pass
    _esp32_socks = {}; _esp32_addrs = {}
    with match_lock:
        mode = match_mode
    robots = [1] if mode == "1v1" else [1, 2]
    ips = {1: C.ROBOT1_IP, 2: C.ROBOT2_IP}
    _ip_to_robot = {ips[idx]: idx for idx in robots}
    for idx in robots:
        _esp32_socks[idx] = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        _esp32_addrs[idx] = (ips[idx], C.ESP32_CMD_PORT)
    print(f"[UDP] ESP32 sockets initialised for robots: {robots}")

def send_command(robot_idx: int, action: int):
    cmd  = C.ACTION_TO_CMD.get(action, C.CMD_STOP)
    sock = _esp32_socks.get(robot_idx); addr = _esp32_addrs.get(robot_idx)
    if sock is None or addr is None: return
    try: sock.sendto(cmd, addr)
    except Exception as e: print(f"[UDP] Robot{robot_idx} send error: {e}")

def stop_all_robots():
    for _ in range(3):
        for idx in list(_esp32_socks.keys()):
            send_command(idx, 0)
        time.sleep(0.01)

# =====================================================================
# SHARED VISION STATE
# =====================================================================

latest_frame    = None
latest_frame_ts = 0.0
frame_lock      = threading.Lock()
frame_available = threading.Event()

homography_matrix     = None
homography_lock       = threading.Lock()
homography_lock_count = 0
_corner_history: dict = {}
_corner_last: dict = {}   # label -> (point, last_seen_ts) for time-windowed lock
_corner_readings: list = []

_USABLE = C.BIRDSEYE_SIZE * (1.0 - 2.0 * C.BIRDSEYE_MARGIN_FRAC)
arena_half_px_warped = _USABLE / 2.0
arena_half_px_raw    = C.ARENA_HALF_PX_DEFAULT
arena_bbox_raw       = None
arena_bbox_lock      = threading.Lock()

balls_tracked: dict = {}
next_ball_id:  int  = 0
balls_lock = threading.Lock()

def _blank_marker():
    return {'center': None, 'front': None, 'last_seen': 0.0}

robot_states: dict = {1: _blank_marker(), 2: _blank_marker()}
human_states: dict = {3: _blank_marker(), 4: _blank_marker()}
robot_lock = threading.Lock()

goal_state    = {'center': None, 'last_seen': 0.0}
owngoal_state = {'center': None, 'last_seen': 0.0}
goal_lock     = threading.Lock()

def _build_aruco_detector():
    """Single arena dictionary (config-driven) + params tuned for small edge tags.

    The whole arena uses ONE dictionary (robots 1-6, corners 46-49, all < 50).
    Reprint corners in this dictionary so they decode alongside the robot tags.
    """
    dict_name = getattr(C, "ARUCO_DICT", "DICT_4X4_50")
    dict_id   = getattr(cv2.aruco, dict_name)
    adict     = cv2.aruco.getPredefinedDictionary(dict_id)

    p = cv2.aruco.DetectorParameters()
    p.minMarkerPerimeterRate    = getattr(C, "ARUCO_MIN_PERIM_RATE", 0.01)
    p.adaptiveThreshWinSizeMin  = getattr(C, "ARUCO_ADAPT_WIN_MIN", 3)
    p.adaptiveThreshWinSizeMax  = getattr(C, "ARUCO_ADAPT_WIN_MAX", 43)
    p.adaptiveThreshWinSizeStep = getattr(C, "ARUCO_ADAPT_WIN_STEP", 4)
    if getattr(C, "ARUCO_CORNER_REFINE", True):
        p.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    # ArUco3 only exists on OpenCV >= 4.7 — guard so older installs don't crash.
    if getattr(C, "ARUCO_USE_ARUCO3", True) and hasattr(p, "useAruco3Detection"):
        p.useAruco3Detection = True

    print(f"[ArUco] Detector: dict={dict_name} aruco3="
          f"{getattr(p, 'useAruco3Detection', False)} "
          f"minPerim={p.minMarkerPerimeterRate} clahe={getattr(C, 'ARUCO_USE_CLAHE', False)}")
    print(f"[Config] file={C.__file__}")
    print(f"[Config] ARENA_CORNER_IDS={C.ARENA_CORNER_IDS}")
    print(f"[Config] Z_FLIP={getattr(C,'Z_FLIP',True)} MIRROR_X={getattr(C,'MIRROR_X',False)} "
          f"HEADING_FLIP={getattr(C,'HEADING_FLIP',False)} "
          f"ACTION_TO_CMD={ {k: v.decode() for k,v in C.ACTION_TO_CMD.items()} }")
    return adict, p, cv2.aruco.ArucoDetector(adict, p)

aruco_dict, aruco_params, aruco_detector = _build_aruco_detector()

# CLAHE for local contrast equalisation before detection (defeats uneven light).
_clahe = (cv2.createCLAHE(clipLimit=getattr(C, "ARUCO_CLAHE_CLIP", 2.0),
                          tileGridSize=(getattr(C, "ARUCO_CLAHE_GRID", 8),
                                        getattr(C, "ARUCO_CLAHE_GRID", 8)))
          if getattr(C, "ARUCO_USE_CLAHE", True) else None)

ACTION_LABELS = {0: 'STOP', 1: 'FWD', 2: 'LEFT', 3: 'RIGHT'}

# Ball-HSV calibration probe throttle (mutable holder so the ball thread can update it)
_ball_hsv_probe = [0.0]

# Corner-missing diagnostic throttle (mutable holder)
_last_corner_warn = [0.0]

# ArUco one-time probe flag (mutable holder)
_aruco_probe_done = [False]
_aruco_probe_count = [0]
_aruco_probe_ts = [0.0]

# =====================================================================
# CAMERA CONTROL STATE  (Unity slider → applied live to the webcam)
# =====================================================================

def _load_camera_settings():
    """Load persisted operator calibration if present, else config defaults."""
    cfg = {"exposure": C.CAMERA_EXPOSURE_DEFAULT,
           "gain":     C.CAMERA_GAIN_DEFAULT,
           "auto":     False}
    try:
        path = getattr(C, "CAMERA_SETTINGS_FILE", None)
        if path and os.path.isfile(path):
            with open(path, "r") as f:
                saved = json.load(f)
            cfg.update({k: saved[k] for k in ("exposure", "gain", "auto") if k in saved})
            print(f"[Camera] Loaded saved settings: {cfg}")
    except Exception as e:
        print(f"[Camera] Could not load settings ({e}); using defaults.")
    return cfg

def _save_camera_settings():
    with _cam_cfg_lock:
        cfg = dict(_cam_cfg)
    try:
        with open(C.CAMERA_SETTINGS_FILE, "w") as f:
            json.dump(cfg, f, indent=2)
        print(f"[Camera] Saved settings to {C.CAMERA_SETTINGS_FILE}: {cfg}")
    except Exception as e:
        print(f"[Camera] Save failed: {e}")

_cam_cfg       = _load_camera_settings()
_cam_cfg_lock  = threading.Lock()
_cam_cfg_dirty = threading.Event()

# Display feed Python → Unity
_display_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

# =====================================================================
# BRAIN
# =====================================================================

class Brain:
    def __init__(self, model_path: str, obs_size: int):
        print(f"[Brain] Loading {model_path}  (expected obs_size={obs_size}) ...")
        if not os.path.isfile(model_path):
            raise FileNotFoundError(
                f"Model file not found: {model_path}\n"
                f"  -> Place the trained .onnx in ai_robot/brains/ as <mode>_<difficulty>.onnx "
                f"(e.g. brains/1v1_easy.onnx, brains/2v2_hard.onnx), or fix config.ONNX_PATHS.")
        try:
            self.session = ort.InferenceSession(model_path, providers=["CPUExecutionProvider"])
        except Exception as e:
            raise RuntimeError(
                f"Failed to load ONNX '{model_path}': {e}\n"
                f"  -> The file is likely CORRUPT (transferred through a text editor). "
                f"Re-export from the training checkpoint and transfer as binary.")
        self.obs_size   = obs_size
        self.input_name = self.session.get_inputs()[0].name
        self.needs_masks = any(i.name == "action_masks" for i in self.session.get_inputs())
        in_shape = self.session.get_inputs()[0].shape
        model_obs = in_shape[-1] if (in_shape and isinstance(in_shape[-1], int)) else None
        if model_obs is not None and model_obs != obs_size:
            raise ValueError(
                f"Obs size mismatch for '{model_path}': model expects {model_obs}, "
                f"this difficulty needs {obs_size}. Wrong model for the difficulty, or this "
                f"is an OLD (pre-redesign) model — not compatible with this runtime.")
        self._first = True
        print(f"[Brain] Loaded OK. Input '{self.input_name}', model_obs={model_obs}")

    def get_action(self, obs):
        feed = {self.input_name: obs}
        if self.needs_masks:
            feed["action_masks"] = np.ones((1, 4), dtype=np.float32)
        outputs = self.session.run(None, feed)
        if self._first:
            for i, out in enumerate(outputs):
                print(f"[Brain] out[{i}] shape={out.shape} vals={out.flatten()[:6]}")
            self._first = False
        names = [o.name for o in self.session.get_outputs()]
        for name in ("deterministic_discrete_actions", "discrete_actions", "action", "output_0"):
            if name in names:
                return int(outputs[names.index(name)].flatten()[0])
        for out in outputs:
            if out.flatten().shape[0] == 1:
                return int(out.flatten()[0])
        return int(np.argmax(outputs[0], axis=-1).flatten()[0])

# =====================================================================
# COORDINATE UTILITIES
# =====================================================================

def _warp_point(pt, H):
    p = np.array([[[pt[0], pt[1]]]], dtype=np.float32)
    w = cv2.perspectiveTransform(p, H)
    return (float(w[0, 0, 0]), float(w[0, 0, 1]))

def _warp_frame(frame, H):
    return cv2.warpPerspective(frame, H, (C.BIRDSEYE_SIZE, C.BIRDSEYE_SIZE),
                               flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_CONSTANT,
                               borderValue=(0, 0, 0))

def _get_homography():
    with homography_lock:
        return homography_matrix

def _warped_arena_centre():
    return (C.BIRDSEYE_SIZE / 2.0, C.BIRDSEYE_SIZE / 2.0)

def _clamp(v, lo=-2.0, hi=2.0):
    return max(lo, min(hi, v))

def _frame_scale():
    """Return (n, cx, cy): normalization half-size and arena centre in the active frame."""
    H = _get_homography()
    if H is not None:
        cx, cy = _warped_arena_centre()
        return arena_half_px_warped, cx, cy
    n = arena_half_px_raw or C.ARENA_HALF_PX_DEFAULT
    with arena_bbox_lock:
        bbox = arena_bbox_raw
    if bbox:
        return n, (bbox[0] + bbox[2]) / 2.0, (bbox[1] + bbox[3]) / 2.0
    return n, 480.0, 270.0

def _warped_heading(center_raw, front_raw, H):
    """Unit heading vector in OBSERVATION space (image X, Z-flipped Y).
    NOTE: MIRROR_X is NOT applied here — it is applied once, centrally, in
    build_observations so positions and heading stay in lock-step."""
    if center_raw is None or front_raw is None:
        return (1.0, 0.0)
    if H is not None:
        c = _warp_point(center_raw, H); f = _warp_point(front_raw, H)
    else:
        c, f = center_raw, front_raw
    dx, dy = f[0] - c[0], f[1] - c[1]
    nrm = math.hypot(dx, dy)
    if nrm < 1e-6:
        return (1.0, 0.0)
    # image X maps to Unity X directly; image Y maps to -Unity Z (Y grows down
    # in pixels, +Z grows away-from-camera in Unity top-down view).
    zf = -1.0 if getattr(C, "Z_FLIP", True) else 1.0
    hx, hy = dx / nrm, zf * dy / nrm
    # HEADING_FLIP kept for emergency manual override; normally False.
    if getattr(C, "HEADING_FLIP", False):
        hx, hy = -hx, -hy
    return (hx, hy)

# =====================================================================
# DISPLAY OVERLAYS
# =====================================================================

def _draw_headings(img, items, H):
    """Draw each marker's front-facing arrow onto a display image.
    items: list of (marker_id, center_raw, front_raw) in raw-image pixels.
    If H is given, points are warped to bird's-eye space first (so the arrow
    matches what the brain actually consumes)."""
    for mid, c, f in items:
        if H is not None:
            c = _warp_point(c, H); f = _warp_point(f, H)
        cx, cy = int(c[0]), int(c[1])
        dx, dy = f[0] - c[0], f[1] - c[1]
        nrm = math.hypot(dx, dy) or 1e-6
        L = 70
        ex, ey = int(cx + dx / nrm * L), int(cy + dy / nrm * L)
        cv2.arrowedLine(img, (cx, cy), (ex, ey), (0, 255, 255), 3, tipLength=0.2)
        cv2.putText(img, str(mid), (cx + 6, cy - 6),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 255), 2)

def _draw_balls(img, balls):
    """Draw a BLUE highlight ring around every detected ball.
    balls: list of (x, y, r) already in the SAME space as img (warped when H
    is active, raw otherwise)."""
    for (bx, by, br) in balls:
        cx, cy = int(bx), int(by)
        rr = max(8, int(br) + 6)
        # soft translucent blue glow
        overlay = img.copy()
        cv2.circle(overlay, (cx, cy), rr, (255, 90, 0), -1)   # BGR: strong blue
        cv2.addWeighted(overlay, 0.25, img, 0.75, 0, img)
        # crisp blue ring + centre dot + label
        cv2.circle(img, (cx, cy), rr, (255, 128, 0), 3)       # blue ring
        cv2.circle(img, (cx, cy), 3, (255, 255, 255), -1)     # centre
        cv2.putText(img, "BALL", (cx + rr + 4, cy),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.5, (255, 128, 0), 2)

def _encode_and_send_display(frame):
    """JPEG-encode a (processed) frame and send to Unity, downscaling if needed
    to fit one UDP datagram."""
    q = C.DISPLAY_JPEG_QUALITY
    cap = getattr(C, "DISPLAY_MAX_UDP_BYTES", 60000)
    img = frame
    for attempt in range(4):
        ok, buf = cv2.imencode(".jpg", img, [cv2.IMWRITE_JPEG_QUALITY, q])
        if not ok:
            return
        data = buf.tobytes()
        if len(data) <= cap:
            try:
                _display_sock.sendto(data, (C.UNITY_IP, C.UNITY_DISPLAY_PORT))
            except Exception as e:
                if not stop_requested:
                    print(f"[Display] Send error: {e}")
            return
        # too big: shrink and/or drop quality, then retry
        img = cv2.resize(img, None, fx=0.8, fy=0.8, interpolation=cv2.INTER_AREA)
        q = max(35, q - 10)
    # gave up silently — a dropped display frame is harmless

# =====================================================================
# OBSERVATION BUILDER  (matches RobotAgent.cs CollectObservations)
# =====================================================================

def build_observations(rc, heading, own_fwd_speed_ms, own_yaw_rate,
                       ball_pos, ball_vel_ms, in_control,
                       goal_center, owngoal_center,
                       opp_center, opp_heading, opp_in_control,
                       role_flag, difficulty):
    """1v1 observation contract (13/19/24 floats) — mirrors RobotAgent.cs's
    CollectObservations() slot-for-slot, egocentric throughout (see
    build_team_observations()'s docstring for the general approach). Own pos
    [0-1] and every *_dist scalar are intentionally left in world-frame /
    frame-independent form, matching CollectObservations() exactly."""
    n, cx, cy = _frame_scale()
    rx, rz = rc
    fwd_x, fwd_z = heading

    # Image Y grows downward; Unity +Z grows upward. Negate Z to match training.
    zf = -1.0 if getattr(C, "Z_FLIP", True) else 1.0
    # Optional single-axis X mirror to un-reflect the world frame. Applied to
    # EVERY x-component (positions, headings, velocity) so the frame stays a
    # proper rotation. See config Section 6.
    mx = -1.0 if getattr(C, "MIRROR_X", False) else 1.0
    hx, hz = mx * fwd_x, fwd_z   # canonical (mirror/flip-corrected) unit heading

    def rel(t):
        dx, dz = mx * (t[0] - rx) / n, zf * (t[1] - rz) / n
        fwd, right = _ego_project(dx, dz, hx, hz)
        return _clamp(fwd), _clamp(right), _clamp(math.hypot(dx, dz), 0.0, 2.0)

    bx, bz = ball_pos if ball_pos else (cx + n * 0.5, cy + n * 0.5)
    gx, gz = goal_center if goal_center else (cx, cy - n * 0.5)
    brx, brz, bd = rel((bx, bz))
    grx, grz, gd = rel((gx, gz))

    vx_w = mx * ball_vel_ms[0] / VEL_SCALE
    vz_w = zf * ball_vel_ms[1] / VEL_SCALE
    vfwd, vright = _ego_project(vx_w, vz_w, hx, hz)
    vx, vz = _clamp(vfwd, -2.0, 2.0), _clamp(vright, -2.0, 2.0)

    obs = [
        _clamp(mx * (rx - cx) / n), _clamp(zf * (rz - cy) / n),  # [0-1] robot pos (unchanged: world-frame, absolute)
        _clamp(own_fwd_speed_ms / ROBOT_VEL_SCALE),               # [2] own fwd speed (was: raw heading x)
        _clamp(own_yaw_rate / YAW_RATE_SCALE),                    # [3] own yaw rate (was: raw heading z)
        brx, brz, bd,                                            # [4-6] ball, egocentric
        vx, vz,                                                  # [7-8] ball velocity, egocentric
        grx, grz, gd,                                            # [9-11] goal, egocentric
        1.0 if in_control else 0.0,                              # [12] in_control
    ]

    if difficulty in ("medium", "hard"):
        if opp_center:
            opx, opz = opp_center
            oprx, oprz, opd = rel((opx, opz))
            # Opponent's OWN heading, projected onto MY axes — NOT their raw
            # world heading. Mirrors RobotAgent.cs's EgoVec(opponent.forward)
            # exactly: a direction-vector rotation, not a position-relative
            # one, so (unlike oprx/oprz above) no /n normalization — it's
            # already a unit-vector dot product, naturally in [-1,1], matching
            # Unity's un-clamped AddObservation(oFwdEgo.x/y).
            ohx, ohz = mx * opp_heading[0], opp_heading[1]
            ofwd, oright = _ego_project(ohx, ohz, hx, hz)
            obs += [oprx, oprz, ofwd, oright, opd,
                    1.0 if opp_in_control else 0.0]                        # [13-18]
        else:
            obs += [0.0, 0.0, 0.0, 0.0, 0.0, 0.0]

    if difficulty == "hard":
        if owngoal_center:
            ogrx, ogrz, ogd = rel(owngoal_center)
        else:
            ogrx, ogrz, ogd = 0.0, 0.0, 2.0
        obs += [ogrx, ogrz, ogd, role_flag]                               # [19-22]
        if ball_pos and owngoal_center:
            bog = math.hypot((ball_pos[0] - owngoal_center[0]) / n,
                             (ball_pos[1] - owngoal_center[1]) / n)
            obs.append(_clamp(bog, 0.0, 2.0))                             # [23]
        else:
            obs.append(2.0)

    return np.array([obs], dtype=np.float32)

def _ego_project(dx, dz, hx, hz):
    """Rotate a canonical world-frame 2D vector (dx, dz) into the robot's own
    facing frame, given its canonical unit heading (hx, hz). Mirrors
    RobotAgent.cs's EgoRel()/EgoVec() exactly: returns (forward-component,
    right-component). (hx, hz) must already have MIRROR_X/Z_FLIP applied —
    see build_team_observations."""
    return dx * hx + dz * hz, dx * hz - dz * hx

def build_team_observations(rc, heading, own_fwd_speed_ms, own_yaw_rate,
                             ball_pos, ball_vel_ms, in_control,
                             goal_center, owngoal_center,
                             teammate_pos, am_nearest,
                             opp1_pos, opp2_pos):
    """2v2 team-mode observation contract (26 floats) — mirrors RobotAgent.cs's
    CollectTeamObservations() slot-for-slot (see Unity/CHANGES.md). Every
    relative vector is egocentric (forward-component, right-component,
    distance) via _ego_project, instead of the raw world-axis deltas
    build_observations() uses for 1v1. A None position (marker not currently
    tracked) yields (0, 0, 0) for that slot, matching RobotAgent.cs's
    null-target fallback in EgoRel()."""
    n, cx, cy = _frame_scale()
    rx, rz = rc
    mx = -1.0 if getattr(C, "MIRROR_X", False) else 1.0
    zf = -1.0 if getattr(C, "Z_FLIP", True) else 1.0

    # Canonical (mirror/flip-corrected) unit heading — same convention as the
    # "mx * fwd_x, fwd_z" heading already used in build_observations().
    hx, hz = mx * heading[0], heading[1]

    def ego_rel(t):
        if t is None:
            return 0.0, 0.0, 0.0
        dx, dz = mx * (t[0] - rx) / n, zf * (t[1] - rz) / n
        fwd, right = _ego_project(dx, dz, hx, hz)
        return _clamp(fwd), _clamp(right), _clamp(math.hypot(dx, dz), 0.0, 2.0)

    obs = [
        _clamp(mx * (rx - cx) / n), _clamp(zf * (rz - cy) / n),          # [0-1] own pos
        _clamp(own_fwd_speed_ms / ROBOT_VEL_SCALE),                       # [2] own fwd speed
        _clamp(own_yaw_rate / YAW_RATE_SCALE),                           # [3] own yaw rate
    ]

    brx, brz, bd = ego_rel(ball_pos)
    obs += [brx, brz, bd]                                                # [4-6] ball, egocentric

    vx, vz = mx * ball_vel_ms[0] / VEL_SCALE, zf * ball_vel_ms[1] / VEL_SCALE
    vfwd, vright = _ego_project(vx, vz, hx, hz)
    obs += [_clamp(vfwd), _clamp(vright)]                                 # [7-8] ball velocity, egocentric

    obs += list(ego_rel(goal_center))                                    # [9-11] scoring goal, egocentric
    obs += list(ego_rel(owngoal_center))                                 # [12-14] own goal, egocentric

    obs.append(1.0 if in_control else 0.0)                               # [15] self in-control

    obs += list(ego_rel(teammate_pos))                                   # [16-18] teammate, egocentric
    obs.append(1.0 if am_nearest else 0.0)                               # [19] am-I-nearest-the-ball

    obs += list(ego_rel(opp1_pos))                                       # [20-22] opponent1, egocentric (fixed)
    obs += list(ego_rel(opp2_pos))                                       # [23-25] opponent2, egocentric (fixed)

    return np.array([obs], dtype=np.float32)

# =====================================================================
# T1 — CAMERA CAPTURE  (Python owns the physical UVC webcam)
# =====================================================================

def _open_camera():
    backend = cv2.CAP_DSHOW if getattr(C, "CAMERA_USE_DSHOW", False) else cv2.CAP_ANY
    cap = cv2.VideoCapture(C.CAMERA_INDEX, backend)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH,  C.CAMERA_WIDTH)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, C.CAMERA_HEIGHT)
    cap.set(cv2.CAP_PROP_FPS,          C.CAMERA_FPS)
    # Keep only the newest frame so a setting change isn't masked by buffered
    # old-exposure frames (best-effort; some backends ignore it).
    try:
        cap.set(cv2.CAP_PROP_BUFFERSIZE, getattr(C, "CAMERA_BUFFERSIZE", 1))
    except Exception:
        pass
    _apply_cam_settings(cap)   # set manual exposure BEFORE the first grab
    return cap

def _apply_cam_settings(cap):
    """Push the current _cam_cfg to the live VideoCapture. Thread-safe."""
    with _cam_cfg_lock:
        cfg = dict(_cam_cfg)
    # Auto-exposure MUST be set first, or CAP_PROP_EXPOSURE is silently ignored.
    cap.set(cv2.CAP_PROP_AUTO_EXPOSURE,
            C.CAMERA_AUTO_EXPOSURE_AUTO if cfg["auto"] else C.CAMERA_AUTO_EXPOSURE_MANUAL)
    if not cfg["auto"]:
        cap.set(cv2.CAP_PROP_EXPOSURE, cfg["exposure"])
        cap.set(cv2.CAP_PROP_GAIN,     cfg["gain"])

def camera_capture_fn():
    global latest_frame, latest_frame_ts
    cap = _open_camera()
    if not cap.isOpened():
        print(f"[Camera] FAILED to open index {C.CAMERA_INDEX}. "
              f"Try another CAMERA_INDEX, check the cable, or close any app using the cam.")
        send_unity_event("disconnect")
        return
    w = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)); h = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    print(f"[Camera] Opened index {C.CAMERA_INDEX} @ {w}x{h}. "
          f"exposure={_cam_cfg['exposure']} gain={_cam_cfg['gain']} auto={_cam_cfg['auto']}")
    fail_count = 0
    last_reassert = time.time()
    reassert_s = getattr(C, "CAMERA_REASSERT_S", 0.0)
    while not stop_requested:
        if _cam_cfg_dirty.is_set():
            _apply_cam_settings(cap)
            _cam_cfg_dirty.clear()
            last_reassert = time.time()
        # Periodically re-push manual settings: some UVC drivers silently revert
        # toward auto-exposure after a hiccup. Skip when the operator chose auto.
        elif reassert_s > 0 and (time.time() - last_reassert) >= reassert_s:
            with _cam_cfg_lock:
                is_auto = _cam_cfg["auto"]
            if not is_auto:
                _apply_cam_settings(cap)
            last_reassert = time.time()
        ok, frame = cap.read()
        if not ok or frame is None:
            fail_count += 1
            if fail_count == 30:
                print("[Camera] Repeated read failures — camera unplugged?")
                send_unity_event("disconnect")
            time.sleep(0.01)
            continue
        fail_count = 0
        with frame_lock:
            latest_frame = frame
            latest_frame_ts = time.time()
        frame_available.set()
    cap.release()
    print("[Camera] Released.")

# =====================================================================
# T2 — CONTROL RECEIVER
# =====================================================================

def control_receiver_fn():
    global robot_started, match_difficulty, match_mode
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("0.0.0.0", C.UNITY_CTRL_PORT)); sock.settimeout(1.0)
    print(f"[Ctrl] Listening on port {C.UNITY_CTRL_PORT}")
    while not stop_requested:
        try:
            data, _ = sock.recvfrom(64)
            msg = data.decode("utf-8").strip()
            if msg.startswith("START"):
                parts = msg.split(":")
                diff = parts[1].lower() if len(parts) > 1 else "easy"
                mode = parts[2].lower() if len(parts) > 2 else "1v1"
                if diff not in ("easy", "medium", "hard"):
                    print(f"[Ctrl] Unknown difficulty '{diff}' -> easy"); diff = "easy"
                if mode not in ("1v1", "2v2"):
                    print(f"[Ctrl] Unknown mode '{mode}' -> 1v1"); mode = "1v1"
                with match_lock:
                    match_difficulty = diff; match_mode = mode
                _init_esp32_sockets()
                with started_lock:
                    robot_started = True
                print(f"[Ctrl] START — difficulty={diff} mode={mode}")
                send_unity_event("heartbeat")
            elif msg == "STOP":
                with started_lock:
                    robot_started = False
                stop_all_robots(); print("[Ctrl] STOP received.")
            elif msg.startswith("CAMERA_SAVE"):
                _save_camera_settings()
            elif msg.startswith("CAMERA"):
                # Format: "CAMERA:<exposure>:<gain>:<auto 0|1>"  e.g. "CAMERA:-6:40:0"
                parts = msg.split(":")
                try:
                    exp  = int(float(parts[1]))
                    gain = int(float(parts[2]))
                    auto = (len(parts) > 3 and parts[3] == "1")
                    exp  = max(C.CAMERA_EXPOSURE_MIN, min(C.CAMERA_EXPOSURE_MAX, exp))
                    gain = max(C.CAMERA_GAIN_MIN,     min(C.CAMERA_GAIN_MAX,     gain))
                    with _cam_cfg_lock:
                        _cam_cfg.update(exposure=exp, gain=gain, auto=auto)
                    _cam_cfg_dirty.set()
                    print(f"[Camera] Set exposure={exp} gain={gain} auto={auto}")
                except (IndexError, ValueError):
                    print(f"[Ctrl] Bad CAMERA msg: {msg}")
        except socket.timeout:
            continue
        except Exception as e:
            if not stop_requested: print(f"[Ctrl] Error: {e}")
    sock.close(); print("[Ctrl] Stopped.")

# =====================================================================
# T3 — ESP32 STATUS RECEIVER
# =====================================================================

def esp32_status_fn():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind(("0.0.0.0", C.ESP32_STATUS_PORT))
    except Exception as e:
        print(f"[ESP32] Status bind failed {C.ESP32_STATUS_PORT}: {e}"); return
    sock.settimeout(1.0)
    print(f"[ESP32] Status listener on port {C.ESP32_STATUS_PORT}")
    while not stop_requested:
        try:
            _data, addr = sock.recvfrom(32)
            idx = _ip_to_robot.get(addr[0])
            if idx is not None:
                with esp32_status_lock:
                    esp32_last_seen[idx] = time.time()
        except socket.timeout:
            continue
        except Exception:
            if not stop_requested: pass
    sock.close(); print("[ESP32] Status listener stopped.")

# =====================================================================
# T4 — ARUCO + HOMOGRAPHY
# =====================================================================

def aruco_thread_fn():
    global homography_matrix, homography_lock_count, arena_half_px_raw, arena_bbox_raw
    corner_pts_raw: dict = {}
    print("[ArUco] Started.")
    while not stop_requested:
        if not frame_available.wait(timeout=1.0):
            continue
        with frame_lock:
            frame = latest_frame.copy() if latest_frame is not None else None
        frame_available.clear()
        if frame is None:
            continue

        now = time.time()
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        # Local contrast equalisation BEFORE detection — mitigates spatially
        # uneven lighting (sunlight/glare on one corner) that a single global
        # camera exposure cannot fix.
        if _clahe is not None:
            gray = _clahe.apply(gray)
        corners, ids, _ = aruco_detector.detectMarkers(gray)

        # ── PROBE: identify the corner markers' dictionary/inversion. ──────────
        if getattr(C, "DEBUG_ARUCO_PROBE", False) and not _aruco_probe_done[0] \
           and homography_matrix is None:
            if now - _aruco_probe_ts[0] >= 3.0:
                _aruco_probe_ts[0] = now
                if _aruco_probe_count[0] == 0:
                    try:
                        cv2.imwrite("aruco_debug_frame.png", frame)
                        cv2.imwrite("aruco_debug_gray.png", gray)
                        print("[ArUco PROBE] Saved aruco_debug_frame.png + aruco_debug_gray.png")
                    except Exception as e:
                        print(f"[ArUco PROBE] Save failed: {e}")
                _aruco_probe_count[0] += 1
                dicts = {
                    "4X4_50": cv2.aruco.DICT_4X4_50, "4X4_100": cv2.aruco.DICT_4X4_100,
                    "4X4_250": cv2.aruco.DICT_4X4_250, "5X5_50": cv2.aruco.DICT_5X5_50,
                    "5X5_250": cv2.aruco.DICT_5X5_250, "6X6_50": cv2.aruco.DICT_6X6_50,
                    "6X6_250": cv2.aruco.DICT_6X6_250, "7X7_50": cv2.aruco.DICT_7X7_50,
                    "ARUCO_ORIGINAL": cv2.aruco.DICT_ARUCO_ORIGINAL,
                    "APRILTAG_36h11": cv2.aruco.DICT_APRILTAG_36h11,
                }
                inv = cv2.bitwise_not(gray)
                print(f"[ArUco PROBE #{_aruco_probe_count[0]}] Trying all dicts (normal + inverted):")
                any_hit = False
                for name, dval in dicts.items():
                    d = cv2.aruco.getPredefinedDictionary(dval)
                    det = cv2.aruco.ArucoDetector(d, cv2.aruco.DetectorParameters())
                    _, n_ids, _ = det.detectMarkers(gray)
                    _, i_ids, _ = det.detectMarkers(inv)
                    nf = sorted(int(x) for x in n_ids.flatten()) if n_ids is not None else []
                    inf = sorted(int(x) for x in i_ids.flatten()) if i_ids is not None else []
                    if nf or inf:
                        any_hit = True
                        print(f"    {name:16s} normal={nf or '—'}  INVERTED={inf or '—'}")
                if not any_hit:
                    print("    — nothing in any dict, either orientation. "
                          "Check aruco_debug_gray.png: markers sharp? enough contrast? "
                          "Try raising exposure brighter.")
                else:
                    print("[ArUco PROBE] ^ A row with your IDs tells you the dict to use "
                          "(and whether markers are inverted).")
                    _aruco_probe_done[0] = True

        corner_pts_raw.clear()

        heading_draw = []   # (marker_id, center_raw, front_raw) for overlay

        if ids is not None:
            # DIAGNOSTIC: show exactly what IDs the detector reads (throttled).
            if getattr(C, "DEBUG_ARUCO_PROBE", False) and homography_matrix is None \
               and (now - _last_corner_warn[0] >= 1.5):
                _last_corner_warn[0] = now
                raw_ids = sorted(int(x) for x in ids.flatten())
                print(f"[ArUco RAW] detector sees IDs: {raw_ids}  "
                      f"(corners expected: {sorted(C.ARENA_CORNER_IDS.keys())}, "
                      f"robots: {C.ARUCO_ID_ROBOT1},{C.ARUCO_ID_ROBOT2})")
            for i, marker_id in enumerate(ids.flatten()):
                marker_id = int(marker_id)   # normalize np.int32 -> int for dict/tuple matching
                mc = corners[i].reshape((4, 2))
                cx = float(np.mean(mc[:, 0])); cy = float(np.mean(mc[:, 1]))
                front = (float((mc[0, 0] + mc[1, 0]) / 2), float((mc[0, 1] + mc[1, 1]) / 2))
                heading_draw.append((marker_id, (cx, cy), front))
                if marker_id in (C.ARUCO_ID_ROBOT1, C.ARUCO_ID_ROBOT2):
                    idx = 1 if marker_id == C.ARUCO_ID_ROBOT1 else 2
                    with robot_lock:
                        robot_states[idx].update(center=(cx, cy), front=front, last_seen=now)
                elif marker_id in (C.ARUCO_ID_HUMAN1, C.ARUCO_ID_HUMAN2):
                    idx = 3 if marker_id == C.ARUCO_ID_HUMAN1 else 4
                    with robot_lock:
                        human_states[idx].update(center=(cx, cy), front=front, last_seen=now)
                elif marker_id == C.ARUCO_ID_GOAL:
                    with goal_lock:
                        goal_state.update(center=(cx, cy), last_seen=now)
                elif marker_id == C.ARUCO_ID_OWN_GOAL:
                    with goal_lock:
                        owngoal_state.update(center=(cx, cy), last_seen=now)
                elif marker_id in C.ARENA_CORNER_IDS:
                    corner_pts_raw[C.ARENA_CORNER_IDS[marker_id]] = (cx, cy)

        required = {"top_left", "top_right", "bottom_left", "bottom_right"}

        # Time-windowed accumulation of corner sightings (arena is fixed).
        fresh_s = getattr(C, "CORNER_FRESH_SECONDS", 0.5)
        for label, pt in corner_pts_raw.items():
            _corner_last[label] = (pt, now)
        fresh = {label: pt for label, (pt, ts) in _corner_last.items()
                 if now - ts <= fresh_s}

        if homography_matrix is None and getattr(C, "DEBUG_OBS", False):
            missing = required - set(fresh.keys())
            if missing and (now - _last_corner_warn[0] >= 2.0):
                _last_corner_warn[0] = now
                print(f"[ArUco] Not locked — missing corners: {sorted(missing)} "
                      f"(fresh<{fresh_s}s: {sorted(fresh.keys())}, "
                      f"this frame: {sorted(corner_pts_raw.keys())})")
        if required.issubset(fresh.keys()):
            for label, pt in fresh.items():
                hist = _corner_history.setdefault(label, [])
                hist.append(pt)
                if len(hist) > C.HOMOGRAPHY_SMOOTH_WINDOW:
                    hist.pop(0)
            def avg(label):
                h = _corner_history[label]
                return (sum(p[0] for p in h) / len(h), sum(p[1] for p in h) / len(h))
            src = np.float32([avg("top_left"), avg("top_right"),
                  avg("bottom_right"), avg("bottom_left")])
            m = C.BIRDSEYE_SIZE * C.BIRDSEYE_MARGIN_FRAC
            s = C.BIRDSEYE_SIZE - m
            dst = np.float32([[m, s], [s, s], [s, m], [m, m]])  # flipped: bottom→top, top→bottom
            H, _ = cv2.findHomography(src, dst, cv2.RANSAC, 5.0)
            if H is not None:
                homography_lock_count += 1
                if homography_lock_count >= C.HOMOGRAPHY_LOCK_FRAMES:
                    with homography_lock:
                        if homography_matrix is None:
                            print("[ArUco] Homography LOCKED — bird's-eye active.")
                        homography_matrix = H
            xs = [p[0] for p in fresh.values()]
            ys = [p[1] for p in fresh.values()]
            half = max(max(xs) - min(xs), max(ys) - min(ys)) / 2.0
            _corner_readings.append(half)
            if len(_corner_readings) > C.CORNER_READINGS_WINDOW:
                _corner_readings.pop(0)
            arena_half_px_raw = sum(_corner_readings) / len(_corner_readings)
            with arena_bbox_lock:
                arena_bbox_raw = (min(xs), min(ys), max(xs), max(ys))
        else:
            homography_lock_count = max(0, homography_lock_count - 1)

        # ── Build the display (bird's-eye when locked, else raw) + overlays ────
        H = _get_homography()
        if getattr(C, "DISPLAY_SHOW_BIRDSEYE", True) and H is not None:
            display = _warp_frame(frame, H)
            draw_H = H
        else:
            display = frame.copy()
            draw_H = None

        # Ball highlight (balls_tracked centres are in the display's space:
        # warped when H active, raw otherwise — same as the ball thread used).
        if getattr(C, "SHOW_BALL_HIGHLIGHT", True):
            with balls_lock:
                ball_draw = [(d['smoothed_center_x'], d['smoothed_center_y'], d['smoothed_radius'])
                             for d in balls_tracked.values()]
            _draw_balls(display, ball_draw)

        if getattr(C, "DEBUG_ARUCO_HEADING", True):
            _draw_headings(display, heading_draw, draw_H)

        # Turn diagnostic for robot 1 (spin debugging). All points here are in
        # DISPLAY space (warped when H active) — balls are stored that way and we
        # warp the robot's centre/front to match, so the helper takes them as-is.
        if getattr(C, "DEBUG_TURN_DIAGNOSTIC", True):
            with robot_lock:
                r1c = robot_states[1]['center']; r1f = robot_states[1]['front']
            with balls_lock:
                bpts = [(d['smoothed_center_x'], d['smoothed_center_y']) for d in balls_tracked.values()]
            if r1c is not None and r1f is not None and bpts:
                r1c_disp = _warp_point(r1c, draw_H) if draw_H is not None else r1c
                r1f_disp = _warp_point(r1f, draw_H) if draw_H is not None else r1f
                nearest_disp = min(bpts, key=lambda p: math.hypot(p[0] - r1c_disp[0], p[1] - r1c_disp[1]))
                _draw_turn_diagnostic_display(display, r1c_disp, r1f_disp, nearest_disp)

        _encode_and_send_display(display)

        if C.DEBUG_SHOW_BIRDSEYE:
            cv2.imshow("Bird's-eye", display); cv2.waitKey(1)
    cv2.destroyAllWindows()
    print("[ArUco] Stopped.")

def _draw_turn_diagnostic_display(img, rc_disp, rf_disp, ball_disp):
    """Turn diagnostic where all points are ALREADY in display (warped) space."""
    if rc_disp is None or ball_disp is None or rf_disp is None:
        return
    dx, dy = rf_disp[0] - rc_disp[0], rf_disp[1] - rc_disp[1]
    nrm = math.hypot(dx, dy) or 1e-6
    zf = -1.0 if getattr(C, "Z_FLIP", True) else 1.0
    hx, hy = dx / nrm, zf * dy / nrm
    if getattr(C, "HEADING_FLIP", False):
        hx, hy = -hx, -hy
    rcx, rcy = int(rc_disp[0]), int(rc_disp[1])
    cv2.line(img, (rcx, rcy), (int(ball_disp[0]), int(ball_disp[1])), (0, 200, 255), 2)
    dbx = ball_disp[0] - rc_disp[0]
    dby = zf * (ball_disp[1] - rc_disp[1])
    dn = math.hypot(dbx, dby) or 1e-6
    facing = (hx * dbx + hy * dby) / dn
    cross = hx * dby - hy * dbx           # obs-space z of heading×dirToBall
    turn = "FWD-OK" if facing > CONTROL_FACING_DOT else ("turn LEFT" if cross > 0 else "turn RIGHT")
    col = (0, 255, 0) if facing > CONTROL_FACING_DOT else (0, 165, 255)
    cv2.putText(img, f"R1 facing={facing:+.2f}  policy-should: {turn}", (12, 28),
                cv2.FONT_HERSHEY_SIMPLEX, 0.7, col, 2)

# =====================================================================
# T5 — BALL DETECTION
# =====================================================================

def ball_thread_fn():
    global balls_tracked, next_ball_id

    def _register(x, y, r, color):
        global next_ball_id
        balls_tracked[next_ball_id] = {
            'center_x': x, 'center_y': y, 'radius': r, 'color': color,
            'last_seen': time.time(),
            'smoothed_center_x': x, 'smoothed_center_y': y, 'smoothed_radius': r}
        next_ball_id += 1

    def _update(lid, x, y, r, color):
        e = balls_tracked.get(lid)
        if e is None:
            _register(x, y, r, color); return
        a = C.BALL_SMOOTHING_ALPHA
        e.update({'center_x': x, 'center_y': y, 'radius': r, 'last_seen': time.time(),
                  'smoothed_center_x': e['smoothed_center_x'] * (1 - a) + x * a,
                  'smoothed_center_y': e['smoothed_center_y'] * (1 - a) + y * a,
                  'smoothed_radius':   e['smoothed_radius']   * (1 - a) + r * a})

    def _match_and_track(detections):
        used = set(); items = list(balls_tracked.items())
        for (x, y, r, color) in detections:
            best_id, best_dist = None, None
            for tid, td in items:
                if tid in used or td['color'] != color:
                    continue
                d = math.hypot(td['center_x'] - x, td['center_y'] - y)
                if best_dist is None or d < best_dist:
                    best_dist, best_id = d, tid
            threshold = max(C.BALL_MATCH_BASE_PX, r * C.BALL_MATCH_RADIUS_MULT)
            if best_id is not None and best_dist < threshold:
                _update(best_id, x, y, r, color); used.add(best_id)
            else:
                _register(x, y, r, color)
        now = time.time()
        for tid in [t for t, d in list(balls_tracked.items())
                    if now - d['last_seen'] > C.BALL_STALE_SECONDS]:
            del balls_tracked[tid]

    print("[Balls] Started.")
    while not stop_requested:
        with frame_lock:
            frame = latest_frame.copy() if latest_frame is not None else None
        if frame is None:
            time.sleep(0.02); continue
        H = _get_homography()
        working = _warp_frame(frame, H) if H is not None else frame
        hsv = cv2.cvtColor(working, cv2.COLOR_BGR2HSV)

        # ── Ball-HSV probe (calibration) ────────────────────────────────────
        if getattr(C, "DEBUG_BALL_HSV", False):
            now_p = time.time()
            if now_p - _ball_hsv_probe[0] >= getattr(C, "DEBUG_OBS_INTERVAL_S", 3.0):
                _ball_hsv_probe[0] = now_p
                lo = np.array(getattr(C, "BALL_HSV_PROBE_LOWER", (80, 80, 80)))
                hi = np.array(getattr(C, "BALL_HSV_PROBE_UPPER", (140, 255, 255)))
                pmask = cv2.inRange(hsv, lo, hi)
                cnt = int(cv2.countNonZero(pmask))
                if cnt > 0:
                    ys, xs = np.where(pmask > 0)
                    med = np.median(hsv[ys, xs], axis=0).astype(int)
                    print(f"[BALL HSV] median H={med[0]} S={med[1]} V={med[2]}  "
                          f"pixels={cnt}  -> suggested blue range: "
                          f"(({max(0,med[0]-15)}, {max(0,med[1]-60)}, {max(0,med[2]-60)}), "
                          f"({min(179,med[0]+15)}, 255, 255))")
                else:
                    print(f"[BALL HSV] no pixels in probe window {tuple(lo)}..{tuple(hi)} "
                          f"— widen BALL_HSV_PROBE_LOWER/UPPER in config.py")

        detections = []; red_mask = None
        for color_name, (lower, upper) in C.HSV_RANGES.items():
            mask = cv2.inRange(hsv, np.array(lower), np.array(upper))
            if color_name == "red_lo":
                red_mask = mask; continue
            if color_name == "red_hi":
                mask = cv2.bitwise_or(mask, red_mask) if red_mask is not None else mask
                canonical = "red"
            else:
                canonical = color_name
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
            mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel)
            mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
            g = cv2.cvtColor(cv2.bitwise_and(working, working, mask=mask), cv2.COLOR_BGR2GRAY)
            _, bm = cv2.threshold(g, 30, 255, cv2.THRESH_BINARY)
            cnts, _ = cv2.findContours(bm, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            for cnt in cnts:
                area = cv2.contourArea(cnt)
                if area < C.BALL_MIN_AREA_PX:
                    continue
                if area > getattr(C, "BALL_MAX_AREA_PX", 1e9):
                    continue
                (cx, cy), r = cv2.minEnclosingCircle(cnt)
                circ = (4 * math.pi * area) / (cv2.arcLength(cnt, True) ** 2 + 1e-6)
                if circ < C.BALL_MIN_CIRCULARITY:
                    continue
                detections.append((cx, cy, r, canonical))
        with balls_lock:
            _match_and_track(detections)
        time.sleep(0.01)
    print("[Balls] Stopped.")

# =====================================================================
# T6 — BRAIN LOOP (re-entrant)
# =====================================================================

def _wait_for_start():
    while not stop_requested:
        with started_lock:
            if robot_started:
                return True
        time.sleep(0.05)
    return False

def brain_thread_fn():
    global stop_requested, robot_started
    print("[Brain] Waiting for START from Unity...")
    while not stop_requested:
        if not _wait_for_start():
            break
        with match_lock:
            difficulty = match_difficulty; mode = match_mode
        print(f"[Brain] START — difficulty={difficulty} mode={mode}")

        # 2v2 uses one fixed 26-float team contract regardless of difficulty
        # (RobotAgent.CollectTeamObservations never branches on trainingMode) —
        # EXPECTED_OBS only covers the 1v1 easy/medium/hard sizes.
        obs_size = TEAM_OBS_SIZE if mode == "2v2" else EXPECTED_OBS.get(difficulty, 13)
        try:
            brain = Brain(C.ONNX_PATHS.get(mode, {}).get(difficulty), obs_size)
        except Exception as e:
            print(f"[Brain] ERROR: {e}")
            send_unity_event("disconnect")
            with started_lock: robot_started = False
            stop_all_robots(); continue

        ai_robots = [1] if mode == "1v1" else [1, 2]

        if mode == "2v2":
            print(f"[Brain] 2v2 — waiting up to {C.STARTUP_TIMEOUT_S}s for both robots...")
            deadline = time.time() + C.STARTUP_TIMEOUT_S; ok = False
            while time.time() < deadline:
                with robot_lock:
                    ok = (robot_states[1]['center'] is not None and
                          robot_states[2]['center'] is not None)
                if ok:
                    print("[Brain] Both AI robots detected."); break
                time.sleep(0.2)
            if not ok:
                print("[Brain] HARD-FAIL: 2v2 but a robot is missing.")
                send_unity_event("wall")
                with started_lock: robot_started = False
                stop_all_robots(); continue

        _run_match(brain, difficulty, mode, ai_robots)
        stop_all_robots()
        print("[Brain] Match ended — waiting for next START.")
    stop_all_robots(); print("[Brain] Stopped.")

def _run_match(brain, difficulty, mode, ai_robots):
    in_control_last  = {i: False for i in ai_robots}
    role_flag        = {i: 0.0   for i in ai_robots}
    robot_lost_since = {i: None  for i in ai_robots}
    action_counts    = {i: {0: 0, 1: 0, 2: 0, 3: 0} for i in ai_robots}
    pos_history      = {i: [] for i in ai_robots}
    recovery_left    = {i: 0 for i in ai_robots}
    prev_motion      = {i: None for i in ai_robots}   # 2v2 only: (t, rc_w, heading_canonical)

    prev_ball_w = None; prev_ball_t = None
    goal_pause_until = 0.0; scoring_armed = True
    last_heartbeat_t = 0.0; last_histogram_t = time.time()
    feed_was_stale = False
    esp32_warned = False
    print("[Brain] Running.")

    while not stop_requested:
        t_start = time.time()
        with started_lock:
            if not robot_started:
                return
        now = time.time()
        if now - last_heartbeat_t >= C.HEARTBEAT_INTERVAL:
            send_unity_event("heartbeat"); last_heartbeat_t = now

        with frame_lock:
            frame_age = now - latest_frame_ts if latest_frame_ts else 1e9
        if frame_age > C.FRAME_STALE_SECONDS:
            if not feed_was_stale:
                print(f"[Safety] Camera feed stale ({frame_age:.1f}s) — stopping.")
                send_unity_event("disconnect"); feed_was_stale = True
            stop_all_robots(); prev_ball_w = None
            time.sleep(C.COMMAND_INTERVAL); continue
        elif feed_was_stale:
            print("[Safety] Camera feed restored."); feed_was_stale = False

        with robot_lock:
            r_states = {i: dict(robot_states[i]) for i in ai_robots}
            h3 = dict(human_states[3]); h4 = dict(human_states[4])
        with goal_lock:
            gc = goal_state['center']; ogc = owngoal_state['center']
        with balls_lock:
            all_balls = [(d['smoothed_center_x'], d['smoothed_center_y'], d['color'])
                         for d in balls_tracked.values()]

        H = _get_homography()
        def warp(pt): return _warp_point(pt, H) if (pt and H is not None) else pt

        # ── GOAL POSITIONS ──────────────────────────────────────────────────
        # The trained model used STATIC goals (Unity AIGoal/OwnGoal transforms),
        # NOT ArUco markers. On a fixed arena the goal never moves, so we use
        # hardcoded bird's-eye positions (config Section 2c) rather than markers
        # 5/6 — which don't exist in training and only add a failure mode. These
        # fractions are ALREADY in warped bird's-eye space, so they are NOT
        # warped again. Falls back to live markers only if USE_FIXED_GOALS is
        # off (or before the homography has locked).
        if getattr(C, "USE_FIXED_GOALS", False) and H is not None:
            sz = C.BIRDSEYE_SIZE
            gc_w  = (C.GOAL_FIXED_X_FRAC     * sz, C.GOAL_FIXED_Z_FRAC     * sz)
            ogc_w = (C.OWN_GOAL_FIXED_X_FRAC * sz, C.OWN_GOAL_FIXED_Z_FRAC * sz)
        else:
            gc_w  = warp(gc); ogc_w = warp(ogc)

        # Ball centres in balls_tracked are ALREADY in bird's-eye space (the ball
        # thread detects on the warped frame). Do NOT warp them again — a second
        # warp pushes the ball off-frame and desyncs it from the robot/goal
        # coordinate space, which was the root cause of the "won't chase" bug.
        balls_w = [(bx, by, col) for (bx, by, col) in all_balls]

        n, cx, cy = _frame_scale()
        meters_per_px   = ARENA_HALF_M / n
        control_dist_px = CONTROL_DIST_M / meters_per_px
        goal_radius_px  = GOAL_RADIUS_M / meters_per_px

        primary = None
        if balls_w:
            r1c = r_states[ai_robots[0]]['center']
            ref = warp(r1c) if r1c else (cx, cy)
            primary = min(((bx, by) for (bx, by, _c) in balls_w),
                          key=lambda p: math.hypot(p[0] - ref[0], p[1] - ref[1]))

        if primary and prev_ball_w and prev_ball_t and (now - prev_ball_t) > 1e-3:
            dt = now - prev_ball_t
            ball_vel_ms = ((primary[0] - prev_ball_w[0]) / dt * meters_per_px,
                           (primary[1] - prev_ball_w[1]) / dt * meters_per_px)
        else:
            ball_vel_ms = (0.0, 0.0)
        prev_ball_w = primary; prev_ball_t = now

        # GLOBAL scoring — BALL position only, ONE count per goal, then pause + re-arm.
        ball_in_any_goal = False
        for (bx, by, _c) in balls_w:
            in_score = bool(gc_w) and math.hypot(bx - gc_w[0], by - gc_w[1]) < goal_radius_px
            in_own   = bool(ogc_w) and math.hypot(bx - ogc_w[0], by - ogc_w[1]) < goal_radius_px
            if in_score or in_own:
                ball_in_any_goal = True
            if scoring_armed and now >= goal_pause_until:
                if in_score:
                    send_unity_event("score_a" if mode == "2v2" else "score")
                    scoring_armed = False; goal_pause_until = now + GOAL_PAUSE_S
                    print(f"[State] AI SCORED — pausing {GOAL_PAUSE_S:.0f}s to reposition.")
                elif in_own:
                    send_unity_event("score_b")
                    scoring_armed = False; goal_pause_until = now + GOAL_PAUSE_S
                    print(f"[State] HUMAN SCORED — pausing {GOAL_PAUSE_S:.0f}s to reposition.")
        if not scoring_armed and now >= goal_pause_until and not ball_in_any_goal:
            scoring_armed = True
            print("[State] Ball cleared — play resumed, scoring re-armed.")

        # Per-robot control + inference
        for idx in ai_robots:
            if now < goal_pause_until:
                send_command(idx, 0)
                continue
            rs = r_states[idx]
            marker_age = now - rs['last_seen'] if rs['last_seen'] else 1e9
            rc_w = warp(rs['center']) if marker_age <= C.MARKER_STALE_SECONDS else None
            if rc_w is None:
                if robot_lost_since[idx] is None:
                    robot_lost_since[idx] = now
                elif now - robot_lost_since[idx] > C.ROBOT_LOST_GRACE_SECONDS:
                    robot_lost_since[idx] = now + 9999
                    send_unity_event("wall")
                    print(f"[State] Robot{idx} lost — wall.")
                send_command(idx, 0); continue
            robot_lost_since[idx] = None

            heading_w = _warped_heading(rs['center'], rs['front'], H)

            nearest = None
            if balls_w:
                nearest = min(((bx, by) for (bx, by, _c) in balls_w),
                              key=lambda p: math.hypot(p[0] - rc_w[0], p[1] - rc_w[1]))

            in_control = False
            if nearest:
                dball = math.hypot(nearest[0] - rc_w[0], nearest[1] - rc_w[1])
                dirx, diry = nearest[0] - rc_w[0], nearest[1] - rc_w[1]
                dn = math.hypot(dirx, diry)
                # NOTE: heading_w already has the z-flip baked in; to dot it with
                # a raw pixel direction we must apply the same z-flip to diry.
                zf = -1.0 if getattr(C, "Z_FLIP", True) else 1.0
                facing = (heading_w[0] * dirx + heading_w[1] * (zf * diry)) / dn if dn > 1e-6 else 0.0
                in_control = (dball < control_dist_px) and (facing > CONTROL_FACING_DOT)
            if in_control and not in_control_last[idx]:
                send_unity_event("pickup")
            in_control_last[idx] = in_control

            opp_state = h3 if (mode == "1v1" or idx == 1) else h4
            opp_age = now - opp_state['last_seen'] if opp_state['last_seen'] else 1e9
            opp_center_w = warp(opp_state['center']) if opp_age <= C.MARKER_STALE_SECONDS else None
            opp_heading = _warped_heading(opp_state['center'], opp_state['front'], H)
            opp_in_control = bool(opp_center_w and nearest and
                                  math.hypot(opp_center_w[0] - nearest[0],
                                             opp_center_w[1] - nearest[1]) < control_dist_px)

            # Self forward-speed / yaw-rate via frame-to-frame differencing —
            # brain_runner.py has no physics engine, so this mirrors how
            # ball_vel_ms is already computed above (position deltas over dt),
            # applied to the robot's own tracked center + heading instead.
            # Needed by BOTH modes now (RobotAgent.cs's 1v1 CollectObservations
            # uses the same self-motion slots [2-3] as team mode).
            mx = -1.0 if getattr(C, "MIRROR_X", False) else 1.0
            zf2 = -1.0 if getattr(C, "Z_FLIP", True) else 1.0
            hx_c, hz_c = mx * heading_w[0], heading_w[1]
            own_fwd_speed_ms, own_yaw_rate = 0.0, 0.0
            prev = prev_motion[idx]
            if prev is not None and (now - prev[0]) > 1e-3:
                dt_m = now - prev[0]
                (prx, prz), (phx, phz) = prev[1], prev[2]
                dxm = mx * (rc_w[0] - prx) * meters_per_px / dt_m
                dzm = zf2 * (rc_w[1] - prz) * meters_per_px / dt_m
                own_fwd_speed_ms, _ = _ego_project(dxm, dzm, hx_c, hz_c)
                sin_dtheta = hx_c * phz - hz_c * phx
                cos_dtheta = hx_c * phx + hz_c * phz
                own_yaw_rate = math.atan2(sin_dtheta, cos_dtheta) / dt_m
            prev_motion[idx] = (now, (rc_w[0], rc_w[1]), (hx_c, hz_c))

            if mode == "2v2":
                other_idx = 2 if idx == 1 else 1
                tm_state = r_states[other_idx]
                tm_age = now - tm_state['last_seen'] if tm_state['last_seen'] else 1e9
                teammate_pos = warp(tm_state['center']) if tm_age <= C.MARKER_STALE_SECONDS else None

                h3_age = now - h3['last_seen'] if h3['last_seen'] else 1e9
                h4_age = now - h4['last_seen'] if h4['last_seen'] else 1e9
                opp1_pos = warp(h3['center']) if h3_age <= C.MARKER_STALE_SECONDS else None
                opp2_pos = warp(h4['center']) if h4_age <= C.MARKER_STALE_SECONDS else None

                am_nearest = True
                if nearest and teammate_pos:
                    my_d = math.hypot(nearest[0] - rc_w[0], nearest[1] - rc_w[1])
                    tm_d = math.hypot(nearest[0] - teammate_pos[0], nearest[1] - teammate_pos[1])
                    am_nearest = (my_d < tm_d) or (my_d == tm_d and idx == 1)

                obs = build_team_observations(
                    rc=rc_w, heading=heading_w,
                    own_fwd_speed_ms=own_fwd_speed_ms, own_yaw_rate=own_yaw_rate,
                    ball_pos=nearest, ball_vel_ms=ball_vel_ms, in_control=in_control,
                    goal_center=gc_w, owngoal_center=ogc_w,
                    teammate_pos=teammate_pos, am_nearest=am_nearest,
                    opp1_pos=opp1_pos, opp2_pos=opp2_pos)
            else:
                if difficulty == "hard":
                    ball_own_half = False
                    if nearest and ogc_w:
                        ball_own_half = ((nearest[0] - cx) * (ogc_w[0] - cx) +
                                         (nearest[1] - cy) * (ogc_w[1] - cy)) > 0.0
                    role_flag[idx] = 1.0 if (ball_own_half or opp_in_control) else 0.0

                obs = build_observations(
                    rc=rc_w, heading=heading_w,
                    own_fwd_speed_ms=own_fwd_speed_ms, own_yaw_rate=own_yaw_rate,
                    ball_pos=nearest, ball_vel_ms=ball_vel_ms,
                    in_control=in_control, goal_center=gc_w, owngoal_center=ogc_w,
                    opp_center=opp_center_w, opp_heading=opp_heading, opp_in_control=opp_in_control,
                    role_flag=role_flag[idx], difficulty=difficulty)

            action = brain.get_action(obs)

            if C.ENABLE_STUCK_RECOVERY:
                hist = pos_history[idx]; hist.append((now, rc_w[0], rc_w[1]))
                while hist and now - hist[0][0] > C.STUCK_WINDOW_S:
                    hist.pop(0)
                if recovery_left[idx] > 0:
                    action = C.STUCK_RECOVERY_ACTION; recovery_left[idx] -= 1
                elif len(hist) >= 2 and action != 0:
                    moved = math.hypot(rc_w[0] - hist[0][1], rc_w[1] - hist[0][2])
                    if (now - hist[0][0]) >= C.STUCK_WINDOW_S and moved < C.STUCK_MOVE_THRESH_PX:
                        recovery_left[idx] = C.STUCK_RECOVERY_STEPS
                        print(f"[Safety] Robot{idx} stuck — recovery turn.")

            action_counts[idx][action] = action_counts[idx].get(action, 0) + 1
            send_command(idx, action)

        if C.DEBUG_OBS and now - last_histogram_t >= C.DEBUG_OBS_INTERVAL_S:
            last_histogram_t = now
            with esp32_status_lock:
                esp = {i: ("up" if (now - esp32_last_seen.get(i, 0)) < 2.0 else "—") for i in ai_robots}
            vmag = math.hypot(*ball_vel_ms)
            print(f"[OBS] H={'LOCKED' if H is not None else 'raw'} balls={len(balls_w)} "
                  f"ball_v={vmag:.2f}m/s diff={difficulty} esp32={esp}")
            if all(v == "—" for v in esp.values()) and not esp32_warned:
                esp32_warned = True
                print("[ESP32] WARNING: no status beacon from any robot. Commands may not be "
                      "reaching it. Check power/WiFi and that ROBOTx_IP matches the ESP32 serial "
                      "output. If the robot never moves, the policy will look 'stuck' even when "
                      "it is issuing correct commands.")
            for idx in ai_robots:
                total = sum(action_counts[idx].values()) or 1
                hist = "  ".join(f"{ACTION_LABELS[a]}:{100*action_counts[idx][a]//total}%" for a in range(4))
                print(f"  Robot{idx}: {hist} control={in_control_last[idx]} role={role_flag[idx]:.0f}")

        time.sleep(max(0, C.COMMAND_INTERVAL - (time.time() - t_start)))

# =====================================================================
# MAIN
# =====================================================================

if __name__ == "__main__":
    print("=" * 72)
    print("  Robot Soccer — Brain Runner  (REDESIGN runtime, new obs contract)")
    print("  >>> BUILD: unified-dict-4x4_50 + ball-highlight + turn-diagnostic <<<")
    print(f"  Unity events {C.UNITY_IP}:{C.UNITY_EVENT_PORT} | display {C.UNITY_DISPLAY_PORT} | "
          f"ctrl {C.UNITY_CTRL_PORT} | esp32-status {C.ESP32_STATUS_PORT}")
    print(f"  Camera: index {C.CAMERA_INDEX} @ {C.CAMERA_WIDTH}x{C.CAMERA_HEIGHT} "
          f"(Python owns the webcam — exposure/gain controllable from Unity)")
    print(f"  Obs sizes: Easy=13 Medium=19 Hard=24 | VEL_SCALE={VEL_SCALE} "
          f"control={CONTROL_DIST_M}m goal_r={GOAL_RADIUS_M}m")
    print("  ── SPIN TEST (do once): watch the on-screen 'R1 facing / policy-should' text.")
    print("     Drive the robot manually and confirm LEFT physically rotates it left.")
    print("     If the text says 'turn LEFT' but the robot never closes the angle, either")
    print("     the ESP32 L/R is inverted (swap ACTION_TO_CMD 2/3) OR the frame is mirrored")
    print("     (set MIRROR_X=True). Use EXACTLY ONE of the two — never both.")
    print("=" * 72)
    print("  Waiting for Unity to send  START:<difficulty>:<mode> ...")
    print("=" * 72)

    threads = [
        threading.Thread(target=camera_capture_fn,   daemon=True, name="Camera"),
        threading.Thread(target=control_receiver_fn, daemon=True, name="CtrlRx"),
        threading.Thread(target=esp32_status_fn,     daemon=True, name="Esp32Rx"),
        threading.Thread(target=aruco_thread_fn,     daemon=True, name="ArUco"),
        threading.Thread(target=ball_thread_fn,      daemon=True, name="Balls"),
        threading.Thread(target=brain_thread_fn,     daemon=True, name="Brain"),
    ]
    for t in threads:
        t.start()
    print("All threads running. Ctrl+C to quit.\n")
    try:
        while not stop_requested:
            time.sleep(0.5)
    except KeyboardInterrupt:
        print("\nCtrl+C — shutting down...")
    stop_requested = True
    time.sleep(0.5)
    stop_all_robots()
    for s in _esp32_socks.values():
        try: s.close()
        except Exception: pass
    _unity_event_sock.close()
    try: _display_sock.close()
    except Exception: pass
    print("Exiting.")