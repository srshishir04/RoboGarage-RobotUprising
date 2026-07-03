"""
camera_probe.py — one-time webcam calibration helper
=====================================================
Run this ONCE per webcam/computer to discover the values to put in config.py:

  • which CAMERA_INDEX is the arena camera
  • whether your driver uses 0.25/0.75 or 1/3 for the auto-exposure manual switch
  • the working EXPOSURE range and its SIGN (drivers differ — README warns of this)
  • a good GAIN

It opens the camera and shows a live window. Keys:

  e / E   exposure  down / up   (step 1)
  g / G   gain      down / up   (step 5)
  a       toggle auto-exposure on/off  (watch if exposure stops responding)
  m       cycle the auto-exposure "manual" magic value (0.25 → 1.0 → 0.0)
  i       print current values to the terminal
  n / N   previous / next camera index (reopens)
  q       quit

Goal (per the reference project): set exposure "as dark as possible while still
usable" to minimise motion blur, then copy the printed values into config.py:
  CAMERA_INDEX, CAMERA_EXPOSURE_DEFAULT, CAMERA_GAIN_DEFAULT,
  CAMERA_AUTO_EXPOSURE_MANUAL
"""

import cv2
import sys

USE_DSHOW = (sys.platform.startswith("win"))   # CAP_DSHOW on Windows, else CAP_ANY

MANUAL_MAGIC_OPTIONS = [0.25, 1.0, 0.0]   # try these if exposure won't respond


def open_cam(index):
    backend = cv2.CAP_DSHOW if USE_DSHOW else cv2.CAP_ANY
    cap = cv2.VideoCapture(index, backend)
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1280)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 720)
    return cap


def main():
    index = 0
    exposure = -6
    gain = 40
    auto = False
    manual_idx = 0

    cap = open_cam(index)
    if not cap.isOpened():
        print(f"Could not open camera {index}. Try 'n' to cycle, or edit the start index.")

    def apply():
        magic = MANUAL_MAGIC_OPTIONS[manual_idx]
        cap.set(cv2.CAP_PROP_AUTO_EXPOSURE, 0.75 if auto else magic)
        if not auto:
            cap.set(cv2.CAP_PROP_EXPOSURE, exposure)
            cap.set(cv2.CAP_PROP_GAIN, gain)

    apply()
    print(__doc__)
    print(f"[probe] Start: index={index} exposure={exposure} gain={gain} "
          f"auto={auto} manual_magic={MANUAL_MAGIC_OPTIONS[manual_idx]}")

    while True:
        ok, frame = cap.read()
        if not ok or frame is None:
            cv2.waitKey(50)
            continue

        # Read back what the driver actually accepted (often clamped/snapped)
        real_exp = cap.get(cv2.CAP_PROP_EXPOSURE)
        real_gain = cap.get(cv2.CAP_PROP_GAIN)
        real_ae = cap.get(cv2.CAP_PROP_AUTO_EXPOSURE)

        hud = frame.copy()
        lines = [
            f"index={index}  auto={'ON' if auto else 'off'}  manual_magic={MANUAL_MAGIC_OPTIONS[manual_idx]}",
            f"exposure set={exposure}  driver_reads={real_exp:.2f}",
            f"gain set={gain}  driver_reads={real_gain:.2f}",
            f"auto_exposure driver_reads={real_ae:.2f}",
            "e/E exposure  g/G gain  a auto  m magic  i print  n/N index  q quit",
        ]
        y = 28
        for ln in lines:
            cv2.putText(hud, ln, (12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
                        (0, 0, 0), 4, cv2.LINE_AA)
            cv2.putText(hud, ln, (12, y), cv2.FONT_HERSHEY_SIMPLEX, 0.6,
                        (0, 255, 0), 1, cv2.LINE_AA)
            y += 28
        cv2.imshow("camera_probe", hud)

        k = cv2.waitKey(1) & 0xFF
        if k == 255:
            continue
        ch = chr(k)

        if ch == 'q':
            break
        elif ch == 'e':
            exposure -= 1; apply()
        elif ch == 'E':
            exposure += 1; apply()
        elif ch == 'g':
            gain = max(0, gain - 5); apply()
        elif ch == 'G':
            gain = min(255, gain + 5); apply()
        elif ch == 'a':
            auto = not auto; apply()
            print(f"[probe] auto-exposure {'ON' if auto else 'off'}")
        elif ch == 'm':
            manual_idx = (manual_idx + 1) % len(MANUAL_MAGIC_OPTIONS)
            apply()
            print(f"[probe] manual magic -> {MANUAL_MAGIC_OPTIONS[manual_idx]} "
                  f"(if exposure now responds, this is your CAMERA_AUTO_EXPOSURE_MANUAL)")
        elif ch == 'i':
            print("\n========== COPY INTO config.py ==========")
            print(f"CAMERA_INDEX                = {index}")
            print(f"CAMERA_EXPOSURE_DEFAULT     = {exposure}")
            print(f"CAMERA_GAIN_DEFAULT         = {gain}")
            print(f"CAMERA_AUTO_EXPOSURE_MANUAL = {MANUAL_MAGIC_OPTIONS[manual_idx]}")
            print(f"  (driver currently reads exposure={real_exp:.2f}, gain={real_gain:.2f})")
            print("=========================================\n")
        elif ch in ('n', 'N'):
            cap.release()
            index = max(0, index + (1 if ch == 'n' else -1))
            cap = open_cam(index)
            apply()
            print(f"[probe] switched to camera index {index} "
                  f"(opened={cap.isOpened()})")

    cap.release()
    cv2.destroyAllWindows()


if __name__ == "__main__":
    main()