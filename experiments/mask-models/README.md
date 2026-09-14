# 안전모 + 마스크 검출 실험 모델

기존 SafetyVision 모델과 애플리케이션 코드는 변경하지 않았다. 이 폴더의 ONNX 파일은 실험용이며 앱에서 자동으로 사용되지 않는다. 두 모델 모두 **얼굴 영역을 먼저 검출해 잘라낸 이미지**가 필요하다.

| 파일 | 출처 | 입력 | 마스크 출력 | 라이선스 |
| --- | --- | --- | --- | --- |
| `face_attrib_net.onnx` | [yakhyo/face-attribute](https://github.com/yakhyo/face-attribute) ([다운로드](https://github.com/yakhyo/face-attribute/releases/download/weights/face_attrib_net.onnx)) | RGB, 128×128, NCHW, float32 `[0,1]`; 비율 유지 후 중앙 여백 | `probability[0,3]` (다섯 독립 속성 중 mask) | [BSD-3-Clause](https://github.com/yakhyo/face-attribute/blob/main/LICENSE) |
| `mask_detector.onnx` | [NahidEbrahimian/Face-Mask-Detection](https://github.com/NahidEbrahimian/Face-Mask-Detection) ([다운로드](https://media.githubusercontent.com/media/NahidEbrahimian/Face-Mask-Detection/main/models/mask_detector.onnx)) | RGB, 128×128, NHWC, float32 `[0,1]` | `dense_1[0]`: 인덱스 0 = 마스크 착용, 1 = 미착용 | 저장소에 명시된 라이선스 없음. 재배포 전 권리 확인 필요 |

두 모델 모두 ONNX Runtime CPU로 정상 로드됨을 확인했다. 각 파일의 SHA-256은 다음과 같다.

- `face_attrib_net.onnx`: `1BF7C6453BEC2FB28E0830F3A76DCEB9FFD020124F87B28DA5355940A7BC6E48`
- `mask_detector.onnx`: `15142CAB6A08B664C68D3B3CB15824B32B631CE593242C1E6391A3597B06DFDA`

2026-09-14, 사용자가 제공한 안전모·마스크 착용 사진에서 얼굴을 수동으로 잘라 시험했다. `face_attrib_net`의 mask 확률은 얼굴 영역 3개에서 `0.99990~0.999999`, `mask_detector`의 마스크 착용 확률은 `0.999999~1.0`이었다. 이 결과는 해당 사진 한 장과 수동 얼굴 영역에만 해당한다. 카메라 전체 화면에서 사용하려면 얼굴 검출, 잘라내기, 전처리, 판정 기준을 별도로 검증해야 한다.
