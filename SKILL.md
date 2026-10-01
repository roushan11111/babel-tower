---
name: babel-tower
description: Install, build, launch, configure, or troubleshoot Babel Tower, the Windows local selection translation app. Use for setting up this app and its Ollama model, changing English/Chinese translation settings, or fixing its inline display. Ordinary translation requests do not require this skill.
---

# Babel Tower / 巴别塔

Set up the bundled Windows app that translates selected text locally and displays the result over its selection. It preserves the underlying document. The app uses UI Automation first, with a conservative local Windows OCR fallback for visible blue selections. It does not use the clipboard or silently switch to cloud translation.

## Use the bundle

Resolve this skill's root from this `SKILL.md`, rather than the caller's working directory. All resources below are relative to that root. Read [README.md](README.md) or [README.zh-CN.md](README.zh-CN.md) when installation details or compatibility limits are needed.

Windows x64, Windows PowerShell 5.1 or later, .NET Framework 4.8, and an installed Ollama are required. Open Ollama before setting up the model or launching the app; its local service must be running. Building uses the Windows .NET Framework compiler. Model weights are a separate download of approximately 1.1 GB; they are not bundled in the skill or executable.

Choose the scripts that match the request:

```powershell
# Explicit first-time model download and import.
& "$skillRoot\scripts\setup-model.ps1"

# Import an existing official model without downloading it.
& "$skillRoot\scripts\setup-model.ps1" -ModelPath "D:\Models\Hy-MT2-1.8B-Q4_K_M.gguf"

# Build from the bundled source when needed.
& "$skillRoot\scripts\build.ps1"

# Launch the app; builds if the bundled executable is absent.
& "$skillRoot\scripts\start.ps1"

# Launch directly into the notification area.
& "$skillRoot\scripts\start.ps1" -Background

# Check an existing build, Ollama, and the installed model without launching.
& "$skillRoot\scripts\start.ps1" -CheckOnly
```

Assign `$skillRoot` to the actual directory containing this file before running these examples. Do not hardcode the original author's machine paths. Scripts require no administrator rights. `start.ps1` checks the running local Ollama service and verified weights before launch. It does not start Ollama or download model weights; if the model is missing, use `setup-model.ps1` as part of the requested installation. `setup-model.ps1 -ModelDirectory` can choose the GGUF download directory. Preserve an existing Ollama installation and model storage configuration. Setup and startup use the local endpoint at `127.0.0.1:11434`.

The app expects the local Ollama alias `swipetranslate-hymt2`, retained for compatibility. Setup uses Tencent's official Hy-MT2-1.8B Q4_K_M GGUF and verifies its SHA256 before importing:

```text
dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
```

## Configure and check

The small settings window offers `英文 → 简体中文` and `简体中文 → 英文` presets. Select one and click `保存并生效`; the next selection uses that direction. Double-click the notification area icon or choose its settings menu to reopen the window. Start the app for the current session; it does not register itself for automatic startup.

Verify the requested behavior in a normal selectable text control, then in the user's target app when desktop interaction is available and authorized. Check selection detection, translation, inline display or popup fallback, and dismissal with Esc or a click elsewhere. Keep text-interface and image-fallback results separate. Image fallback selects an installed Windows OCR language matching the configured source: English or simplified Chinese for the two presets. A missing language produces a clear message rather than using the wrong recognizer. Small text and certain Chinese glyphs can still be misread. Text selections exposed by UI Automation do not need an OCR language pack.

## Maintain the app

Source is in `assets/app/`; the executable builds to `dist/BabelTower.exe`. Make changes there and use `scripts/build.ps1`. Keep request cancellation and selection revalidation: a translation must not cover a selection that changed while inference was running. Keep the OCR mask restricted to accepted highlighted glyphs and exclude password/protected input.

Report what was actually verified: compilation, generated-image OCR, local model inference, or a user-observed display are different checks. Do not claim all-software compatibility, guaranteed accuracy, instant latency, or desktop validation from a build or synthetic image test. If desktop tools are unavailable, finish the build and give the user a concise manual check.

Created by [@HanJaKKK](https://x.com/HanJaKKK). App code and skill use [MIT](LICENSE); model weights and other dependencies have separate terms in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
