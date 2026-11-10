# Face grouping: models and the reference check

Models (both free for commercial use, from the OpenCV model zoo):

| File | Model | Licence | Source |
|---|---|---|---|
| `face_detection_yunet_2023mar.onnx` | YuNet face detector | MIT | https://huggingface.co/opencv/face_detection_yunet |
| `face_recognition_sface_2021dec.onnx` | SFace face recogniser | Apache-2.0 | https://huggingface.co/opencv/face_recognition_sface |

On the server they live in `/opt/apps/models` (mounted at `/app/models`), next to the background-removal model.
`./fetch.sh <dir>` downloads both and checks their SHA-256.

## Checking the C# pipeline against OpenCV

`FaceAnalyzerTests` compares `FaceAnalyzer` with OpenCV's own `FaceDetectorYN` + `FaceRecognizerSF` on
public-domain photos. The expected values come from running OpenCV (`reference.py`), never typed by hand.

```sh
./fetch.sh ~/.cache/faces-models
./samples.sh ~/.cache/faces-samples           # public-domain NASA / White House portraits
python3 -m venv /tmp/fv && /tmp/fv/bin/pip install opencv-python-headless numpy
/tmp/fv/bin/python -I reference.py ~/.cache/faces-models ~/.cache/faces-samples   # writes ref.json there
FACE_MODELS=~/.cache/faces-models FACE_SAMPLES=~/.cache/faces-samples dotnet test --filter FaceAnalyzerTests
```
