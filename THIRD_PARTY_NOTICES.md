# Third-party components

Babel Tower's application code and skill are licensed under MIT. Model weights,
the inference runtime, and Windows components are separate dependencies and
are not included in the source repository or download packages.

- Translation model: Tencent [Hy-MT2-1.8B-GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF), official Q4_K_M file. The model repository declares Apache-2.0; consult its current license when obtaining the weights.
- Inference runtime: [Ollama](https://github.com/ollama/ollama), installed separately from its official distribution.
- Windows UI Automation, WinForms and Windows.Media.Ocr: supplied by Windows/.NET. Available OCR languages depend on the installed Windows language packs.

Author: [@HanJaKKK](https://x.com/HanJaKKK).
