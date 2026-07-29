"""
config.py — Robot Soccer Project  (REDESIGN — pairs with new RobotAgent.cs + brain_runner.py)
=============================================================================================
Single source of truth. brain_runner.py imports this and never needs editing.

SECTIONS
  1. Network — IPs and ports
  2. Robots — ArUco IDs and ESP32 addresses
  3. Arena — physical dimensions and corner marker layout
  4. Homography — bird's-eye warp (smoothed)
  5. Models — ONNX paths per difficulty (resolved next to this file)
  6. Vision — HSV colour ranges, detection thresholds, coordinate handedness
  7. Physics contract — MUST MATCH RobotAgent.cs (control/goal/velocity)
  8. Brain — timing
  9. Safety — staleness watchdogs + optional stuck recovery
 10. Debug

PHYSICS CONTRACT (Section 7) is the new, critical part: these five numbers must
equal the matching fields in RobotAgent.cs, or sim-to-real breaks. They are:
  ARENA_HALF_M ↔ arenaHalfSize
  CONTROL_DIST_M ↔ controlDistance
  CONTROL_FACING_DOT ↔ controlFacingDot
  GOAL_RADIUS_M ↔ goalRadius
  VEL_SCALE ↔ velScale
"""

import os

_HERE = os.path.dirname(os.path.abspath(__file__))

# ─────────────────────────────────────────────────────────────────────────────
# 1. NETWORK
# ─────────────────────────────────────────────────────────────────────────────

UNITY_IP         = "127.0.0.1"
UNITY_EVENT_PORT = 4211   # Python → Unity   (heartbeat / score / score_b / pickup / wall / disconnect)
UNITY_FRAME_PORT = 4212   # DEPRECATED — Unity no longer sends frames. Python now OWNS the camera
                          #   (see Section 1b). Kept only so old references don't break; unused.
UNITY_CTRL_PORT  = 4213   # Unity  → Python   (START:<diff>:<mode> / STOP / CAMERA:exp:gain:auto / CAMERA_SAVE)
UNITY_DISPLAY_PORT = 4215 # Python → Unity   (annotated JPEG feed for the GameScene display)

# ─────────────────────────────────────────────────────────────────────────────
# 1b. CAMERA  — Python OWNS the physical UVC webcam directly (cv2.VideoCapture).
#     This is what lets the Unity exposure slider reach REAL hardware exposure/
#     gain (WebCamTexture in Unity cannot). A USB cam can only be opened by ONE
#     process — so Unity must NOT also open it. Unity now RECEIVES the feed on
#     UNITY_DISPLAY_PORT instead of capturing.
# ─────────────────────────────────────────────────────────────────────────────

CAMERA_INDEX     = 0       # OpenCV device index. Try 1, 2, ... if 0 is wrong/not found.
CAMERA_WIDTH     = 1280    # 1280x720 recommended; higher = more CPU for detection.
CAMERA_HEIGHT    = 720
CAMERA_FPS       = 30
CAMERA_USE_DSHOW = True     # Windows: CAP_DSHOW backend exposes exposure reliably.
                            # Set False on Linux (uses V4L2 via CAP_ANY).

# Exposure / gain. SIGN AND RANGE ARE DRIVER-DEPENDENT — these are starting
# points. Run camera_probe.py once to discover the right values for YOUR webcam,
# then update these. The operator fine-tunes live from the Unity slider; "Save"
# persists the chosen values to camera_settings.json (NOT this file).
#
# AUTO-EXPOSURE HANDSHAKE (the #1 gotcha): on most UVC drivers CAP_PROP_EXPOSURE
# is IGNORED unless CAP_PROP_AUTO_EXPOSURE is first set to the "manual" magic
# value. On many drivers that's 0.25 (manual) / 0.75 (auto); on some it's 1 / 3.
CAMERA_AUTO_EXPOSURE_MANUAL = 1
CAMERA_AUTO_EXPOSURE_AUTO   = 0.75

CAMERA_EXPOSURE_DEFAULT = -7     # "as dark as possible while still usable" (reduces motion blur)
CAMERA_EXPOSURE_MIN     = -13    # slider lower bound  (more negative = darker on many drivers)
CAMERA_EXPOSURE_MAX     = 0      # slider upper bound
CAMERA_GAIN_DEFAULT     = 25    # mid-range; corners need adequate light (gain=6
                                # got saved earlier and was far too dark)
CAMERA_GAIN_MIN         = 0
CAMERA_GAIN_MAX         = 255

# Keep only the newest frame in the driver buffer (no stale buffered frames
# after an exposure change). 1 = tightest coupling between a setting change and
# the next grabbed frame.
CAMERA_BUFFERSIZE       = 1
# Some UVC drivers silently drift back toward auto-exposure after a USB hiccup
# or timeout. Re-assert the manual exposure/gain this often (seconds) so the
# camera cannot quietly revert mid-match. 0 disables.
CAMERA_REASSERT_S       = 3.0

# Persisted operator calibration (written by "Save", loaded at startup if present).
CAMERA_SETTINGS_FILE = os.path.join(_HERE, "camera_settings.json")

# Display feed Python → Unity
DISPLAY_JPEG_QUALITY = 70    # 40–80; lower = less bandwidth
DISPLAY_SEND_EVERY_N = 1     # send 1-in-N processed frames (raise to throttle)
DISPLAY_SHOW_BIRDSEYE = True # True = stream the warped bird's-eye view; False = raw frame
DISPLAY_MAX_UDP_BYTES = 60000  # JPEG larger than this is downscaled before send (UDP datagram cap)

# ─────────────────────────────────────────────────────────────────────────────
# 2. ROBOTS
# ─────────────────────────────────────────────────────────────────────────────

ESP32_CMD_PORT    = 4210   # Python → ESP32   (motor command byte: F/L/R/S)
ESP32_STATUS_PORT = 4214   # ESP32  → Python   (status beacon "OK")

ROBOT1_IP = "192.168.8.188"   # AI Robot 1  (ArUco ID 1) — set from ESP32 serial output
ROBOT2_IP = "192.168.8.149"   # AI Robot 2  (ArUco ID 2) — set from ESP32 serial output

ARUCO_ID_ROBOT1   = 1    # AI robot 1
ARUCO_ID_ROBOT2   = 2    # AI robot 2 (2v2)
ARUCO_ID_HUMAN1   = 3    # Human robot 1
ARUCO_ID_HUMAN2   = 4    # Human robot 2 (2v2)
ARUCO_ID_GOAL     = 5    # Scoring goal — AI shoots here (ball here → AI scores)
ARUCO_ID_OWN_GOAL = 6    # Defended goal — human shoots here (ball here → human scores)

# Arena corner ArUco IDs → physical position (from camera image, top-left origin)
# Verified against camera screenshot:
#   49 = top-left      48 = top-right
#   47 = bottom-left   46 = bottom-right
ARENA_CORNER_IDS = {
    49: "top_left",
    48: "top_right",
    47: "bottom_left",
    46: "bottom_right",
}

# ─────────────────────────────────────────────────────────────────────────────
# 2c. FIXED GOAL POSITIONS  (sim-to-real critical)
#     The trained model used STATIC goals — Unity AIGoal/OwnGoal transforms at
#     fixed arena corners — NOT ArUco markers. On a fixed physical arena the
#     goal never moves, so we hardcode its bird's-eye position instead of
#     detecting markers 5/6 (which don't exist in the training scene and only
#     add a failure mode). brain_runner uses these directly in bird's-eye space
#     (they are NOT warped again).
#
#     Values are fractions of BIRDSEYE_SIZE (0=left/top, 1=right/bottom),
#     MEASURED live by placing the ball at each net centre and reading the
#     bird's-eye position. To re-calibrate, place the ball at each goal and
#     divide its warped pixel by BIRDSEYE_SIZE.
#
#     Verified consistent with training (Z_FLIP=True, MIRROR_X=True):
#       scoring goal → obs (+X,+Z) = Unity AIGoal (orange, top-right)
#       own goal     → obs (−X,−Z) = Unity OwnGoal (dark,   bottom-left)
# ─────────────────────────────────────────────────────────────────────────────

USE_FIXED_GOALS        = True    # False → fall back to live ArUco markers 5/6
GOAL_FIXED_X_FRAC      = 0.136   # scoring goal (AI attacks / shoots into)
GOAL_FIXED_Z_FRAC      = 0.124
OWN_GOAL_FIXED_X_FRAC  = 0.874   # own goal (AI defends)
OWN_GOAL_FIXED_Z_FRAC  = 0.865

# ─────────────────────────────────────────────────────────────────────────────
# 2b. ARUCO DETECTION  — ONE dictionary for the whole arena.
#     Robots/goals use IDs 1–6, corners use 46–49 — all < 50, so a single
#     DICT_4X4_50 detector covers everything. 4X4_50 is chosen deliberately:
#     a SMALL dictionary has a LARGE inter-marker Hamming distance, so bit
#     errors (glare, blur, edge distortion) on the fixed corner references are
#     corrected rather than misread. Reprint corner markers in 4X4_50 (IDs
#     46–49) so they match this detector.
# ─────────────────────────────────────────────────────────────────────────────

ARUCO_DICT = "DICT_4X4_50"        # name of a cv2.aruco.DICT_* constant

# Detector tuning. Reverted to OpenCV STOCK DEFAULTS — this matches the
# previous working CV script (v4_multithreaded_computer_vision.py), which used
# a bare DetectorParameters() and locked reliably on this arena. The earlier
# "small edge tag" tuning (aruco3 + low minPerim + wide window + CLAHE) was
# REJECTING the clear corner markers, so it is all turned off here.
ARUCO_USE_ARUCO3     = False       # was True — ArUco3 dropped small edge tags
ARUCO_MIN_PERIM_RATE = 0.03        # OpenCV default (was 0.02/0.01)
ARUCO_ADAPT_WIN_MIN  = 3           # default
ARUCO_ADAPT_WIN_MAX  = 23          # default (was 43)
ARUCO_ADAPT_WIN_STEP = 10          # default (was 4)
ARUCO_CORNER_REFINE  = False       # match working file (it set nothing)

# CLAHE OFF — on the dark textured mat it amplified background noise and hurt
# detection. The frame is already clean enough (see aruco_debug_gray.png).
ARUCO_USE_CLAHE      = False
ARUCO_CLAHE_CLIP     = 2.0
ARUCO_CLAHE_GRID     = 8

CMD_STOP    = b'S'
CMD_FORWARD = b'F'
CMD_LEFT    = b'L'
CMD_RIGHT   = b'R'

# Action index (from the policy) -> single command byte sent to the ESP32.
#   0=STOP 1=FORWARD 2=LEFT 3=RIGHT
# QUICK SOFTWARE FIX for an inverted turn: if you confirm (see brain_runner
# on-screen test) that the robot turns the WRONG way — policy LEFT makes it go
# right — swap the two lines below instead of re-flashing the ESP32:
#     2: CMD_RIGHT, 3: CMD_LEFT
# Do this OR fix the .ino, never both (two swaps cancel out).
ACTION_TO_CMD = {0: CMD_STOP, 1: CMD_FORWARD, 2: CMD_LEFT, 3: CMD_RIGHT}

# ─────────────────────────────────────────────────────────────────────────────
# 3. ARENA
# ─────────────────────────────────────────────────────────────────────────────

ARENA_PHYSICAL_SIZE_M  = 1.5     # physical side length (metres)
ARENA_HALF_PX_DEFAULT  = 251     # raw-px fallback half-size until 4 corners seen
CORNER_READINGS_WINDOW = 8

# ─────────────────────────────────────────────────────────────────────────────
# 4. HOMOGRAPHY — BIRD'S-EYE WARP
# ─────────────────────────────────────────────────────────────────────────────

BIRDSEYE_SIZE            = 720
BIRDSEYE_MARGIN_FRAC     = 0.05
HOMOGRAPHY_LOCK_FRAMES   = 5
HOMOGRAPHY_SMOOTH_WINDOW = 10    # smooth corner points before computing H
# A corner counts toward the lock if seen within this many seconds — lets the
# homography lock even when the four corners never decode in the SAME frame
# (normal for small edge tags). The arena is fixed, so this is safe.
CORNER_FRESH_SECONDS     = 0.5

# ─────────────────────────────────────────────────────────────────────────────
# 5. MODELS  (resolved next to this file)
# ─────────────────────────────────────────────────────────────────────────────

# Rename your new Easy export RobotAgent.onnx → Easy.onnx, or change "easy" below.
ONNX_PATHS = {
    "easy":   os.path.join(_HERE, "Easy.onnx"),
    "medium": os.path.join(_HERE, "Medium.onnx"),
    "hard":   os.path.join(_HERE, "Hard.onnx"),
}

# Observation sizes for the REDESIGNED models. MUST match RobotAgent.cs
# Behavior Parameters → Vector Observation Space Size.
OBS_SIZES = {
    "easy":   13,
    "medium": 19,
    "hard":   24,
}

# ─────────────────────────────────────────────────────────────────────────────
# 6. VISION
# ─────────────────────────────────────────────────────────────────────────────

# RECALIBRATE under real arena lighting before deployment.
# NOTE: your on-screen [BALL HSV] probe reports median H≈98 S≈200 V≈228 on the
# ball, so this range (H 86–116) is well centred. Good.
HSV_RANGES = {
    "blue":   ((86, 170, 115), (116, 255, 255)),
    #"orange": ((5,   130, 150), (20,  255, 255)),
}

BALL_MIN_AREA_PX       = 60 #80
BALL_MAX_AREA_PX = 2000
BALL_MIN_CIRCULARITY   = 0.55 #0.5
BALL_SMOOTHING_ALPHA   = 0.70
BALL_STALE_SECONDS     = 0.40
BALL_MATCH_BASE_PX     = 30
BALL_MATCH_RADIUS_MULT = 1.5

# Real soccer = ONE ball (matches the redesigned training). The runtime uses the
# nearest detected ball as "the" ball.
EXPECTED_BALL_COUNT = 1

# ── COORDINATE HANDEDNESS (sim-to-real critical) ────────────────────────────
# Marker heading orientation. Flip if the robot drives opposite its ArUco front
# edge (i.e. the on-screen arrow points out the BACK of the robot).
HEADING_FLIP = False  # 180° flip of the heading vector (negates both x AND z)

# Image Y grows downward; Unity +Z grows upward (top-down view). Negate every Z
# component in build_observations so real-arena coords match training space.
# VERIFIED correct on this arena: with Z_FLIP=True the scoring goal (id 5) lands
# at obs +Z and the own goal (id 6) at obs −Z, matching RobotAgent.cs.
Z_FLIP       = True

# MIRROR the X axis of EVERY observation (robot/ball/goal/opponent positions,
# headings and velocities). Leave False first. Turn True ONLY if the on-screen
# diagnostic shows the robot spinning the wrong way to face the ball AND you have
# already confirmed the ESP32 turn direction is correct (see the test procedure
# printed by brain_runner). A single-axis mirror fixes a reflected world frame —
# the classic "chases its tail forever" sim-to-real bug. Never combine this with
# an ACTION_TO_CMD L/R swap; use exactly one of the two.
MIRROR_X     = True

# ─────────────────────────────────────────────────────────────────────────────
# 7. PHYSICS CONTRACT — MUST MATCH RobotAgent.cs  (sim-to-real critical)
# ─────────────────────────────────────────────────────────────────────────────

ARENA_HALF_M       = 0.75    # ↔ RobotAgent.cs arenaHalfSize (real arena 1.5m → 0.75)
CONTROL_DIST_M     = 0.10    # ↔ controlDistance — ball "in control" within this range
CONTROL_FACING_DOT = 0.30    # ↔ controlFacingDot — ball must be in front (dot threshold)
GOAL_RADIUS_M      = 0.20    # ↔ goalRadius — ball within this of a goal = scored
VEL_SCALE          = 1.5     # ↔ velScale — m/s that maps to a ball-velocity obs of 1.0

# ─────────────────────────────────────────────────────────────────────────────
# 8. BRAIN
# ─────────────────────────────────────────────────────────────────────────────

COMMAND_HZ       = 20
COMMAND_INTERVAL = 1.0 / COMMAND_HZ

HEARTBEAT_INTERVAL = 1.0      # Python → Unity heartbeat cadence (s)
STARTUP_TIMEOUT_S  = 10.0     # 2v2: hard-fail if a required robot marker isn't seen
GOAL_PAUSE_S       = 6.0      # robot stops this long after a goal for repositioning

# ─────────────────────────────────────────────────────────────────────────────
# 9. SAFETY — staleness watchdogs + optional stuck recovery
# ─────────────────────────────────────────────────────────────────────────────

FRAME_STALE_SECONDS      = 1.0   # no new frame → stop all robots + "disconnect"
MARKER_STALE_SECONDS     = 1.5   # marker unseen this long → entity not visible
ROBOT_LOST_GRACE_SECONDS = 3.0   # debounce before a lost robot escalates to "wall"

# Optional stuck recovery — OFF during validation so you see the raw policy.
ENABLE_STUCK_RECOVERY = False
STUCK_WINDOW_S        = 2.0
STUCK_MOVE_THRESH_PX  = 20.0
STUCK_RECOVERY_STEPS  = 10
STUCK_RECOVERY_ACTION = 3        # 3 = RIGHT

# ─────────────────────────────────────────────────────────────────────────────
# 10. DEBUG
# ─────────────────────────────────────────────────────────────────────────────

DEBUG_OBS            = True
DEBUG_OBS_INTERVAL_S = 3.0
DEBUG_SHOW_BIRDSEYE  = True
DEBUG_BALL_HSV       = False             # turn ON to print the ball's true HSV (OFF for matches)
DEBUG_ARUCO_PROBE    = True              # one-time: if NO markers detected, dump frame + try all dicts
DEBUG_ARUCO_HEADING  = True              # draw each marker's heading arrow on the display
SHOW_BALL_HIGHLIGHT  = True              # draw a blue highlight ring around every detected ball
DEBUG_TURN_DIAGNOSTIC = True             # draw robot→ball line + facing/turn readout (spin debugging)
BALL_HSV_PROBE_LOWER = (80, 80, 80)      # wide search window for "anything blue-ish"
BALL_HSV_PROBE_UPPER = (140, 255, 255)