# 서버 모델 파일

`appsettings.json`에서 사용하는 ONNX 파일은 이 폴더에 둔다. `.gitignore` 때문에 ONNX 파일 자체는 GitHub에 올라가지 않는다.

| 파일 | 역할 | 출처 |
| --- | --- | --- |
| `safetyvision_v2_896.onnx` | 안전모·안전조끼·마스크 검출 | [SafetyVision YOLOv8 v2](https://huggingface.co/ayushgupta7777/safetyvision-yolov8/tree/main/v2) |
| `yolov8n.onnx` | 사람 검출 | [Kalray YOLOv8n](https://huggingface.co/Kalray/yolov8/tree/main) — 로컬 파일에 COCO `names` 메타데이터 추가 |
| `face_detection_yunet_2023mar.onnx` | 얼굴 위치 검출 실험용 (현재 사용 안 함) | [OpenCV Zoo YuNet](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) |
| `face_attrib_net.onnx` | 마스크 분류 실험용 (현재 사용 안 함) | [FaceAttribNet](https://github.com/yakhyo/face-attribute) |

현재 `main`의 마스크 판정은 PPE 모델의 Mask/NO-Mask 후보를 사용한다. `appsettings.json`의 `MaskModelPath` 설정은 남아 있지만 별도 분류기 모델은 로드하지 않는다.
