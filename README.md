# Babel Tower / 巴别塔

Select text on Windows, release the mouse, and see a local translation over the selection.

Babel Tower is a Windows desktop app and a reusable Codex skill. Its translation engine is Tencent Hy-MT2-1.8B running through Ollama on your own computer. The inline display is a visual overlay: the original text in the source app stays intact.

Created by **[@HanJaKKK](https://x.com/HanJaKKK)**. [中文说明](README.zh-CN.md)

![Rendered preview of the Babel Tower settings window](assets/settings-preview.png)

Settings preview rendered from the production controls. See [validation and limitations](VALIDATION.md).

## What it does

- Translates English → Chinese and Chinese → English, with a small settings window; saving changes the direction immediately.
- Reads the actual text selection through Windows UI Automation where the source app supports it.
- Falls back to local Windows OCR for supported visible blue text selections, using an installed English or simplified Chinese recognizer according to the selected direction.
- Shows the translation over the selection when it fits, or displays the full result in a nearby popup.
- Dismisses the display when you click elsewhere or press **Esc**. The notification area menu can pause translation or open settings.
- Rechecks the selection before showing the result, so changed or unconfirmed selections are abandoned.

The shipped app uses the local Ollama endpoint. It does not read the clipboard, send selected text or images to a cloud translator, or switch providers after an error. Initial model download and Ollama's own installation/update traffic require a connection; translation and OCR run locally after setup.

## Requirements

- Windows 10/11 x64.
- Windows PowerShell 5.1 or later and .NET Framework 4.8.
- [Ollama for Windows](https://ollama.com/download/windows), installed and running locally before model setup or app startup.
- Enough space for the approximately **1.1 GB** model download and Ollama's imported model copy.
- The matching Windows English or simplified Chinese OCR language pack for the image fallback. Text selections read through UI Automation do not need an OCR pack. If the selected language is unavailable, the app reports it rather than using another language's recognizer.

A compatible GPU can accelerate the local model. CPU operation is possible; actual latency depends on hardware, model loading, text length, and other workloads. No latency or accuracy guarantee is implied.

## Quick start

Download and extract a Windows package from [Releases](https://github.com/roushan11111/babel-tower/releases), or clone the source:

```powershell
git clone https://github.com/roushan11111/babel-tower.git
Set-Location .\babel-tower
```

Install and open Ollama, then explicitly prepare the model once:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1
```

This downloads the official model, verifies its SHA256, and imports it into local Ollama. The downloaded GGUF is stored under `%LOCALAPPDATA%\BabelTower\models` by default. To use another download directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1 -ModelDirectory "D:\BabelTower\models"
```

If you already have the official GGUF, import it without downloading another copy:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1 -ModelPath "D:\Models\Hy-MT2-1.8B-Q4_K_M.gguf"
```

Launch Babel Tower:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1
```

The startup script builds `dist/BabelTower.exe` if necessary, then checks the running local Ollama service and verified model weights before launch. Open Ollama first. The app warms up the local model, so the first request may take longer. The startup script does not start Ollama or download a model automatically. Setup and startup use `127.0.0.1:11434`.

In the small settings window, click **英文 → 简体中文** (English → Simplified Chinese), then **保存并生效** (Save and apply), and select a sentence in another app. To reverse the direction, reopen settings from the notification area icon, click **简体中文 → 英文** (Simplified Chinese → English), and save. The next selection uses the saved direction. You can close the settings window and keep Babel Tower running in the notification area. To start there directly, add `-Background` to the startup command. Automatic startup at sign-in is not configured.

To check an existing build, the running Ollama service, and verified model weights without launching the app or starting Ollama:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -CheckOnly
```

## Install as a Codex skill

This repository is also the skill folder. Clone it into your configured Codex skill directory under the name `babel-tower`. On a default Windows installation:

```powershell
$babelSkillDirectory = Join-Path $env:USERPROFILE '.codex\skills\babel-tower'
git clone https://github.com/roushan11111/babel-tower.git $babelSkillDirectory
```

If you use a custom `CODEX_HOME`, place the folder under that directory's `skills` subfolder. Refresh skill discovery or restart Codex, then ask:

```text
Use $babel-tower to set up and launch local English-to-Chinese selection translation on this Windows computer.
```

The skill installs, builds, launches, configures, and troubleshoots this app. It is not a general-purpose translation prompt. Installing the skill alone does not install Ollama or model weights.

## Build from source

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The compiler and platform libraries come from Windows/.NET Framework. No Visual Studio, Node.js, Python, or cloud credentials are required for the app build. The output is `dist/BabelTower.exe`.

```text
SKILL.md                  Codex skill entry point
agents/openai.yaml        Skill UI metadata
assets/app/               C# application sources and Windows manifest
scripts/build.ps1         Build the executable
scripts/start.ps1         Check prerequisites and launch
scripts/setup-model.ps1   Explicit model download or verified local import
dist/BabelTower.exe        Locally built Windows executable / release asset
```

## Compatibility and validation

Selection access depends on the source app. Browsers and editors that expose Windows text-selection interfaces are generally the best candidates. Custom canvases, game UIs, protected input, non-blue highlights, obscured text, and some elevated apps may not work. The OCR fallback can decline unclear or incomplete selections; small text and some Chinese glyphs can still be misread. Translation is not guaranteed to preserve every nuance, technical term, or punctuation mark.

Development checks covered compilation, generated-image selection/OCR, local model inference, and selection changes during translation. Inline Chinese display was observed in a Notepad test and confirmed by the user in the target desktop workflow. The new settings window and Chinese → English desktop flow still need manual verification. Compatibility with other apps depends on their selection interfaces and display behavior.

If nothing appears, wait for the first model warmup, then try one full English sentence in a standard text control. The notification area **View runtime status** item reports the current stage and gesture counts without recording source text. If a selection changes during translation, retry that selection. If you only receive a popup, the result may not fit inside the original area or inline display may be disabled.

## Model and credits

Application code and the Codex skill are released under the [MIT License](LICENSE). Model weights, Ollama, and Windows components are separate dependencies; see [third-party notices](THIRD_PARTY_NOTICES.md).

- Creator: [@HanJaKKK on X / Twitter](https://x.com/HanJaKKK).
- Translation model: [Tencent Hy-MT2-1.8B GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF), Q4_K_M. The official model card lists Apache-2.0; model weights are not bundled in this repository.
- Local inference: [Ollama](https://github.com/ollama/ollama).
- Image recognition: [Windows Media OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine).

The local model alias remains `swipetranslate-hymt2` for compatibility with existing installations. The setup script verifies the official GGUF against:

```text
dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
```
