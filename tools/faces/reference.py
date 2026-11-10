# Runs OpenCV's FaceDetectorYN + FaceRecognizerSF on the samples: the expected values for FaceAnalyzerTests.
import cv2, numpy as np, json, sys, glob, os
det = cv2.FaceDetectorYN.create(sys.argv[1] + "/face_detection_yunet_2023mar.onnx", "", (640, 640), 0.8, 0.3, 5000)
rec = cv2.FaceRecognizerSF.create(sys.argv[1] + "/face_recognition_sface_2021dec.onnx", "")
out = {}
for p in sorted(glob.glob(sys.argv[2] + "/*.jpg")):
    k = os.path.basename(p)[:-4]
    img = cv2.imread(p)
    h, w = img.shape[:2]; sc = 640 / max(h, w)
    small = cv2.resize(img, (round(w * sc), round(h * sc)), interpolation=cv2.INTER_AREA)
    canvas = np.zeros((640, 640, 3), np.uint8); canvas[:small.shape[0], :small.shape[1]] = small
    _, faces = det.detect(canvas)
    res = []
    for f in (faces if faces is not None else []):
        crop = rec.alignCrop(canvas, f)
        e = rec.feature(crop).flatten(); e = e / np.linalg.norm(e)
        res.append({"box": [float(v) / sc for v in f[:4]], "score": float(f[14]), "emb": e.tolist()})
    out[k] = res
    print(k, len(res), [round(r["score"], 3) for r in res])
json.dump(out, open(sys.argv[2] + "/ref.json", "w"))
emb = [(k, i, np.array(r["emb"])) for k, rs in out.items() for i, r in enumerate(rs) if k != "cabinet"]
for a in emb:
    print(f"{a[0]}#{a[1]:<2}", " ".join(f"{float(a[2] @ b[2]):5.2f}" for b in emb))
