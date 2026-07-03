import cv2
d = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
for mid in (46, 47, 48, 49, 5, 6):           # corners + goals
    img = cv2.aruco.generateImageMarker(d, mid, 800)   # 800px, big & crisp
    cv2.imwrite(f"4x4_50_id{mid}.png", img)