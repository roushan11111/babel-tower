# Validation for v0.1.0

Date: 2026-10-01. This is an initial Windows release, not a guarantee of compatibility with every application.

## Current release checks

- The final production sources compile with the Windows x64 .NET Framework C# compiler.
- 30 OCR checks passed, covering language selection, installed English/simplified Chinese recognition, missing-language rejection and the compatible English-only overload.
- 37 settings and local-model integration checks passed. They cover saving/reloading both translation directions, invalid settings and failed writes, six Chinese-to-English model examples, cache use, and two generated Chinese blue-selection → Windows OCR → actual local model → full overlay-layout examples.
- The settings form was rendered offscreen and its controls checked. The preview in the README is a render of the production controls, not a live desktop screenshot; selected ComboBox captions were rendered from the real controls' text because native printing omitted them.
- An independent skill check built from a separate directory and performed a read-only local readiness check. The skill frontmatter and metadata validation passed.
- The model setup script rejected an invalid file before import, and successfully verified/imported an existing official GGUF while reusing Ollama's existing weights layer. The readiness check confirms the exact weight blob at the fixed loopback endpoint. It does not launch Ollama or load the model for inference.
- Normal startup preserves an already-running selection translator instead of silently starting a second instance or terminating it.

Example verified with the actual local model:

> 这项工作很大，所以我会分阶段完成。

Result:

> This task is very large, so I will complete it in stages.

The generated-image example “这是中文翻译测试” was recognized locally and translated as “This is a Chinese translation test.” These are functional examples, not a stable latency or general accuracy benchmark.

## Known limitations retained during testing

Other generated Chinese text, including “识别与翻译都在本机完成。”, produced incorrect characters or radicals in some blue-selection masks. Increasing the font size did not consistently fix it. Those failures were retained during development; the successful examples above do not establish complete OCR accuracy. A matching repeated OCR result can still contain the same recognition error.

UI Automation is preferred when the source application exposes its actual selected text. OCR falls back only for supported blue highlights, and can miss words or punctuation. Translation can also mistranslate technical terminology.

## Desktop observations

The earlier English-to-Chinese version displayed an overlay in a Notepad test. The user later confirmed an overlay in their GPT window, with runtime state reaching `covered`. The GPT host type (browser or desktop client) was not independently established.

The new settings UI and Chinese-to-English path have been checked offscreen and with the local model. Their live desktop flow has not yet been independently verified. These records do not prove support for all browsers, GPT clients or other software.
