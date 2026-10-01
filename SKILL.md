---
name: babel-tower
description: Build, launch, configure, or troubleshoot Babel Tower, the enhanced Windows local translator. Use for its Ollama setup, selection overlay, right panel, input-draft translation, or paired Chrome/Edge webpage extension. Ordinary translation requests do not require this skill.
---

# Babel Tower / 巴别塔

Maintain the existing enhanced translator as Babel Tower v0.2.0. Preserve its UI Automation selection reading and conservative local Windows OCR fallback for visible blue selections. Selection display is a visual overlay that leaves the document intact. The right panel also retains source/result and has an independent manual-input tab. Explicitly clicking an input's `译` button replaces the entire draft and leaves it unsent. Browser-page translation is provided by the paired extension. All these paths use local inference without clipboard reads or cloud fallback.

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

The small window is titled `巴别塔 · 本地增强版 0.2.0`. It keeps `英文 → 简体中文` and `简体中文 → 英文` presets and offers the model's language entries, with `自动识别` for source only. Select the direction and click `保存并生效`; the next request uses a snapshot of the saved direction. The same window enables the right panel, native input button and browser bridge. Double-click the tray icon or use its settings menu to reopen. The app does not register automatic startup.

For the right panel, use `打开右侧译窗` or its saved enable flag. Its `划选译文` and `输入翻译` tabs have separate state. `保存语言` changes only the direction and preserves existing display, OCR and connection flags. On an upgrade, exit an already-running enhanced translator before launching the replacement; preserve the shared single-instance guard and compatible model alias.

Verify the requested behavior in a normal selectable text control, then the user's target app when desktop interaction is available. Check selection detection, translation, overlay or nearby popup, right-panel retention, and Esc/click dismissal of the overlay. Keep text-interface and image-fallback results separate. Image fallback matches an installed Windows OCR source language. Automatic-source OCR uses English/Chinese target hints; other directions may require explicit source selection. A missing language reports an error instead of using another recognizer. Small text and certain Chinese glyphs can still be misread. UI Automation selections do not need an OCR pack.

## Input and webpage workflows

The native input button supports writable non-password Win32 `Edit` fields with usable `ValuePattern`. Do not claim every desktop application's chat box supports it. Clicking `译` reads the complete draft, translates locally, and writes back only after focus, field identity and unchanged text have been checked. Never substitute clipboard/keystroke pasting or send the message automatically. A changed draft, focus, disabled field or unsupported control abandons the replacement.

For Chrome/Edge functionality, read [browser-extension/README.md](browser-extension/README.md). The folder `browser-extension/` is bundled but not automatically installed. Guide loading it through the browser's extension manager and **Load unpacked**, or operate that UI if the requested setup and available tools allow it; do not edit browser profiles as a workaround.

Enable and save `浏览器网页翻译连接（需要扩展）`, then open `网页翻译 · 连接浏览器` to view the per-installation 64-hex pairing code. Enter it in the extension's connection options and select `保存并检查连接`. The fixed bridge is `http://127.0.0.1:17863`; it forwards only to local inference. The token lives in local `browser-bridge.json` beside the executable and trusted extension `storage.local`. Do not include that file or code in published artifacts, webpage DOM or diagnostics.

The extension's toolbar popup offers `一键翻译当前网页`, `停止翻译`, `还原原文`, and saved language settings. Supported focused input/textarea/plain-contenteditable fields get a small `译` button; translated text remains a draft. Page translation skips input/code/protected areas and retains originals for restoration. Existing pages may need refresh after extension loading. Internal browser pages, extension stores, PDFs, embedded frames (iframes) and complex editors are outside this version's support.

Browser webpage/input translation requests wait at most 25 seconds. A timeout preserves unfinished original text; retry after model warmup or with shorter input. Stopping prevents writeback and later page-node processing, while model computation already submitted may continue briefly. Do not report a stop as guaranteed immediate GPU cancellation.

A local listener failure does not invalidate selection translation or the right panel. Windows can deny the bridge URL listener even when its TCP port is free. Report the distinction from a missing model or unpaired extension. The app does not elevate itself or change system URL ACLs automatically.

## Maintain the app

Application source is in `assets/app/`, extension source in `browser-extension/`, and the executable builds to `dist/BabelTower.exe`. Use `scripts/build.ps1` after C# changes. Keep selection revalidation, input identity/text/focus rechecks, request snapshots, cancellation, and separate manual drafts. Preserve explicit save semantics and all settings flags when copying options. Keep OCR restricted to accepted highlighted glyphs and exclude password/protected input.

When changing the bridge, preserve its fixed loopback endpoint, pairing authentication, extension-only origin checks, bounded requests and cancellation across stop/restart. Webpage replacement and restoration must check that the target DOM text still matches the recorded snapshot. Model language choices are not proof of equal accuracy. Exclude settings, bridge tokens, logs, screenshots of actual selected user text, and model weights from a public package.

Report compilation, generated-image OCR, actual local-model inference, controlled extension fixtures and user-observed display as separate checks. Read [VALIDATION.md](VALIDATION.md) for release evidence. Do not claim all-software compatibility, guaranteed accuracy, instant latency, or user GPT compatibility from a build or controlled fixture. If desktop tools are unavailable, finish the build and give the user a concise manual check.

Created by [@HanJaKKK](https://x.com/HanJaKKK). App, extension and skill use [MIT](LICENSE); model weights and other dependencies have separate terms in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
