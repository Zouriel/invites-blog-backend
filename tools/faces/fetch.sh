#!/usr/bin/env bash
# Downloads the two face models into $1 and checks them.
set -euo pipefail
dir="${1:?folder}"
mkdir -p "$dir"
curl -sSLf -o "$dir/face_detection_yunet_2023mar.onnx" "https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx"
curl -sSLf -o "$dir/face_recognition_sface_2021dec.onnx" "https://huggingface.co/opencv/face_recognition_sface/resolve/main/face_recognition_sface_2021dec.onnx"
cd "$dir"
sha256sum -c - <<'SUMS'
8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4  face_detection_yunet_2023mar.onnx
0ba9fbfa01b5270c96627c4ef784da859931e02f04419c829e83484087c34e79  face_recognition_sface_2021dec.onnx
SUMS
