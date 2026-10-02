# Validation for v0.2.3

## v0.2.3 author attribution

Date: 2026-10-01. Added the user-provided Douyin account `YZRJ88` alongside the existing Twitter attribution in settings, the expanded panel, extension UI and documentation. It is plain account text; no unverified profile URL is provided.

- Production sources compile as **0.2.3.0**. The settings and panel client controls were rendered without showing a test window or reading user content. Both account labels were visually checked for complete display; native ComboBox captions were rendered from their real control text where native printing omitted them.
- The local app was replaced with the compiled build and restarted with its settings window. Saved settings remained byte-for-byte unchanged.
- This is a text/layout update. The earlier 61-check results remain evidence for the preceding behavior changes; they were not reported as a new v0.2.3 full regression run.

## Earlier v0.2.2 local evidence

## v0.2.2 original launcher appearance

Date: 2026-10-01. The launcher now uses a deep-blue rounded tile, an original stepped-tower drawing and an internal cyan status line. The earlier pink circle, large translation glyph and external check badge were removed. Gesture handling and panel behavior are unchanged.

- All production sources compile as **0.2.2.0**; the existing **61 production-control checks passed again** with controlled translation delegates and test windows outside the physical screens.
- The new production-control preview was visually inspected. It includes the tower, BABEL label and status line; it is an offscreen render, not a desktop capture.
- The local app was replaced with the checked build and restarted. The saved settings remained byte-for-byte unchanged. The updated skill and Windows packages contain the current source/preview; historical packages are retained separately.
- The physical mouse-hook and mixed-DPI validation boundaries documented below remain unchanged.

## Earlier v0.2.1 local evidence

## v0.2.1 local floating entry

Date: 2026-10-01. The right-side entry is now a compact, borderless circular launcher. New selection results update retained content without opening the panel. Clicking opens a borderless panel beside the launcher; collapse/Esc retains the draft and language choices.

- The full production sources compile as Windows x64 .NET Framework application version **0.2.1.0**.
- **61 isolated production-control checks passed:** 32 existing panel checks and 29 floating-entry checks. These cover explicit opening/collapse, no automatic opening after selection results, drag versus click, capture-loss state reset, right-edge placement on negative-origin and small synthetic monitors, enable/disable, unread state, preservation of manual drafts/language choices, cancellation and stale responses, ownership guards and disposal. Translation delegates were controlled stubs; no network requests or user settings writes occurred.
- All test windows were shown outside the physical screens. Explicit nonactivating test opening preserved the user's foreground window. This is component evidence, not a physical mouse-hook end-to-end test.
- The actual local v0.2.1 app was started in background mode. Read-only inspection confirmed its **64 × 64** launcher visible at the desktop's right edge and its process responsive. The existing settings file remained byte-for-byte unchanged, including the user's translation direction. A read-only local readiness check confirmed Ollama and the verified model alias.
- `assets/floating-launcher-preview.png` and `assets/right-panel-preview.png` are offscreen renders of production controls with synthetic content. They were visually inspected and do not show user-selected text or a user's external application.
- Physical drag/click interaction, display removal and mixed-DPI monitors have not been manually verified. An unexpected mouse-capture loss resets and redocks the button, but does not reposition an already open panel until the next normal dock/open action. The application's existing system-DPI mode is retained.

These records describe local builds and checks; public-release artifacts are listed separately on GitHub Releases. Previous model, browser-extension and compatibility evidence below belongs to the earlier release.

## Earlier v0.2.0 evidence

Date: 2026-10-01. v0.2.0 unifies the existing local enhanced selection translator with a right panel, native draft button and paired browser extension. The records below distinguish isolated component checks, actual local-model calls, browser fixtures and earlier user observations.

## v0.2.0 checks

- The combined application sources compile with the Windows x64 .NET Framework C# compiler.
- The v0.2.0 application was launched and its responsive window title `巴别塔 · 本地增强版 0.2.0` confirmed. This establishes startup, not the target-application workflows listed below.
- **26 settings checks passed.** The updated 420 × 592 settings window includes language direction, overlay/popup choice, local OCR, right-panel retention, native input button and browser connection. Checks cover the new flags and explicit save behavior; the updated settings preview is an offscreen render of production controls.
- **32 right-panel checks passed.** Actual form controls were exercised with a controlled translation delegate. Checks cover separate selection/manual state, language saving without losing other preferences, failure reporting, cancellation on edit/direction change/close/dispose, rejection of stale replies, external direction refresh, pinning and right-edge bounds on small or negative-origin screens. This set does not benchmark actual model speed.
- **41 isolated native-input checks passed.** These exercise the conservative native input controller, eligibility/recheck behavior, draft replacement and failure/cancellation handling. They establish component behavior, not compatibility with every desktop application's input field.
- **66 production-bridge checks passed.** Checks cover pairing authentication, origins, request limits, settings, stop/restart, cancellation of in-flight translation when stopped and prevention of stale success after restart. The model prompt names for all 38 target-language entries were checked; this does not evaluate their translation accuracy. One actual local Hy-MT2 request translated the synthetic input example below.
- **45 isolated browser-extension checks passed in headless Edge with the actual Manifest V3 extension.** A work-only mock bridge handled **28 synthetic requests**. Checks cover page DOM translation/restoration, focused input/textarea replacement, React-style value handling, plain contenteditable, changed drafts/focus, cancellation, a real 25-second timeout, four-node batch limits, focus/composition changes inside beforeinput handlers and unsupported/protected cases. Browser fixtures used the mock bridge, not the real translation model and not the user's GPT session.

Actual local-model input example:

> 这是一条输入框翻译测试。

Result:

> This is a translation test for input fields.

The settings and right-panel preview files use synthetic text. They render the actual production controls offscreen and do not show a user application or prove a live desktop workflow. Settings ComboBox captions are rendered from their real control text when native printing omits them.

The bridge uses fixed `127.0.0.1:17863`; Ollama remains at `127.0.0.1:11434`. Pairing codes and `browser-bridge.json` are private local configuration and are excluded from public packages.

## Windows connection boundary

Windows may deny `HttpListener` URL access even if the TCP port is free. The connection failure path distinguishes access denial from a port/startup failure. The app does not elevate itself or change system URL ACLs automatically. Selection translation and the right panel remain usable if the browser listener cannot start; extension pairing does not resolve a denied listener permission.

## Earlier v0.1.0 checks

These checks were completed for the earlier release. They remain historical evidence; they are not new v0.2.0 tests of every added language or workflow.

- The v0.1.0 production sources compiled with the Windows x64 .NET Framework C# compiler.
- 30 OCR checks passed, covering language selection, installed English/simplified Chinese recognition, missing-language rejection and the compatible English-only overload.
- 37 settings and local-model integration checks passed. They cover saving/reloading both translation directions, invalid settings and failed writes, six Chinese-to-English model examples, cache use, and two generated Chinese blue-selection → Windows OCR → actual local model → full overlay-layout examples.
- The v0.1.0 settings form was rendered offscreen and its controls checked. That release's preview used the production controls; selected ComboBox captions were rendered from the real controls' text because native printing omitted them.
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

## Desktop observations and outstanding manual checks

The earlier English-to-Chinese version displayed an overlay in a Notepad test. The user later confirmed an overlay in their GPT window, with runtime state reaching `covered`. The GPT host type (browser or desktop client) was not independently established.

The earlier settings UI and Chinese-to-English path were checked offscreen and with the local model. v0.2.0's combined global desktop flow, native `译` button in the user's target applications, and loaded browser-extension workflow on the user's GPT page have not yet been manually verified. The isolated Edge fixture does not establish GPT editor compatibility. These records do not prove support for every browser, desktop client, language pair or editing control.
