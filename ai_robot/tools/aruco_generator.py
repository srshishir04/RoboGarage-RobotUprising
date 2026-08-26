"""Regenerates the 4 corner ArUco markers (IDs 46-49) into ai_robot/Aruco Markers/,
alongside the rest of the pre-generated, ready-to-print marker set (robot/opponent
IDs 1-4, generated separately — see ai_robot/README.md). Writes there explicitly
(not the current working directory) so it always lands in the same place
regardless of where you run this script from.
"""
import os
import cv2

_MARKERS_DIR = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Aruco Markers")

d = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
for mid in (46, 47, 48, 49):
    img = cv2.aruco.generateImageMarker(d, mid, 800)   # 800px, big & crisp
    out_path = os.path.join(_MARKERS_DIR, f"Corner_4x4_50_id{mid}.png")
    cv2.imwrite(out_path, img)
    print(f"wrote {out_path}")
