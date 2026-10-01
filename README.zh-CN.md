# 巴别塔 Babel Tower

在 Windows 中划选文字，松开鼠标，译文就显示在选区位置。

巴别塔是一款 Windows 桌面工具，也是一份可复用的 Codex skill。它通过本机 Ollama 运行腾讯 Hy-MT2-1.8B 翻译模型。原位显示采用视觉覆盖，来源软件里的原文仍然保留。

作者：**[@HanJaKKK](https://x.com/HanJaKKK)**。 [English README](README.md)

![巴别塔设置窗口渲染预览](assets/settings-preview.png)

这是正式控件的离屏渲染预览，验证范围和已知限制见 [验证说明](VALIDATION.md)。

## 功能

- 支持英文 → 中文、中文 → 英文。小设置窗口保存后，翻译方向立即生效。
- 优先通过 Windows 文字接口读取真正选中的文字。
- 文字接口无法提供选区时，对支持的蓝色选区进行本地 OCR，按翻译方向选择已安装的英文或简体中文识别器。
- 译文能放下时显示在原选区，放不下时通过附近的浮窗显示完整内容。
- 点击别处或按 **Esc** 收起。托盘菜单可以暂停翻译或打开设置。
- 翻译完成后再次确认选区；选区变化或无法确认时，放弃这次覆盖。

发布的应用使用本机 Ollama，不读取剪贴板，不把划选文字或图片发送到云端翻译，也不会在出错后自动换成在线服务。首次下载模型及 Ollama 的安装、更新需要联网；准备完成后的翻译和识别在本机运行。

## 运行条件

- Windows 10/11，64 位。
- Windows PowerShell 5.1 或更新版本，以及 .NET Framework 4.8。
- 已安装 [Windows 版 Ollama](https://ollama.com/download/windows)，准备模型和启动应用前先打开，让本地服务保持运行。
- 为约 **1.1 GB** 的模型文件及 Ollama 导入后的模型副本留出空间。
- 图像识别后备需要对应的 Windows 英文或简体中文 OCR 语言包。通过文字接口读取选区不需要 OCR 语言包。所选语言未安装时会明确提示，不会换用其他语言的识别器。

兼容的显卡可以加速模型，CPU 也可以运行。实际等待时间受硬件、首次加载、文字长度及其他程序占用影响，不保证瞬间完成或每句都准确。

## 开始使用

从 [Releases](https://github.com/roushan11111/babel-tower/releases) 下载并解压 Windows 发布包，或获取源码：

```powershell
git clone https://github.com/roushan11111/babel-tower.git
Set-Location .\babel-tower
```

安装并打开 Ollama 后，明确运行一次模型准备脚本：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1
```

脚本会下载官方模型、校验 SHA256，并导入本机 Ollama。下载的 GGUF 默认保存在 `%LOCALAPPDATA%\BabelTower\models`。也可以指定下载目录：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1 -ModelDirectory "D:\BabelTower\models"
```

已经有官方 GGUF 文件时，可以直接导入，不重新下载：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-model.ps1 -ModelPath "D:\Models\Hy-MT2-1.8B-Q4_K_M.gguf"
```

启动：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1
```

缺少 `dist/BabelTower.exe` 时，启动脚本会先编译，再检查正在运行的本地 Ollama 服务和已校验的模型权重，然后启动应用。请先打开 Ollama。应用会预热本地模型，第一次使用可能需要多等一会儿。启动脚本不会代为打开 Ollama 或自动下载模型；模型准备和启动使用 `127.0.0.1:11434`。

在小设置窗口中点击 **英文 → 简体中文**，再点击 **保存并生效**，然后去其他软件划选一句文字。要切换中译英，从托盘图标重新打开设置，点击 **简体中文 → 英文** 并保存，下一次划选就使用新的方向。关闭设置窗口后，工具继续留在托盘中。启动命令添加 `-Background` 可以直接进入托盘；应用不会自行设置开机启动。

只检查已有构建、正在运行的 Ollama 和已校验的模型权重，不启动应用或 Ollama：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -CheckOnly
```

## 安装成 Codex skill

整个仓库就是 skill 文件夹。将其克隆到自己的 Codex skills 目录，文件夹命名为 `babel-tower`。默认 Windows 配置可以使用：

```powershell
$babelSkillDirectory = Join-Path $env:USERPROFILE '.codex\skills\babel-tower'
git clone https://github.com/roushan11111/babel-tower.git $babelSkillDirectory
```

如果设置了自己的 `CODEX_HOME`，放到该目录的 `skills` 子目录中。刷新 skill 列表或重新启动 Codex，然后可以说：

```text
使用 $babel-tower，在这台 Windows 电脑上配置并启动英文划选翻译成中文。
```

这份 skill 用于安装、构建、启动、配置和排查巴别塔应用。只安装 skill 不会同时安装 Ollama 或模型文件。

## 从源码构建

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

应用使用 Windows/.NET Framework 自带的编译器和系统库构建，无须 Visual Studio、Node.js、Python 或云端密钥。结果位于 `dist/BabelTower.exe`。

仓库主要文件：

| 路径 | 用途 |
| --- | --- |
| `SKILL.md` | Codex skill 入口 |
| `agents/openai.yaml` | Skill 展示信息 |
| `assets/app/` | C# 源码及 Windows 清单 |
| `scripts/build.ps1` | 编译应用 |
| `scripts/start.ps1` | 检查条件并启动 |
| `scripts/setup-model.ps1` | 明确下载模型或校验导入已有文件 |
| `dist/BabelTower.exe` | 本地构建结果及发布附件 |

## 兼容范围与验证情况

能否读取选区取决于来源软件。提供 Windows 文字选区接口的浏览器和编辑器通常更适合。自绘画布、游戏界面、受保护输入、非蓝色高亮、文字被遮挡的区域及部分管理员权限应用可能不支持。本地 OCR 可能拒绝不清晰或不完整的选区，小字号及部分中文字形仍可能被错识别。翻译不能保证专业术语、语义和标点完全准确。

开发检查覆盖了编译、合成图像选区/OCR、本机模型调用，以及翻译期间的选区变化。原位中文显示在记事本测试中被观察到，并由用户在目标桌面使用过程中确认。新增加的小设置窗口及中文 → 英文桌面流程仍需手动验证。其他软件的兼容情况取决于其选区接口和显示方式。

没有出现译文时，先等待首次模型预热，再在普通文字控件中完整划选一行英文。托盘里的“查看运行状态”会显示阶段和划选次数，不记录选区原文。翻译期间选区变化时，重新划选即可。出现浮窗可能是译文放不进原选区，或原位覆盖已关闭。

## 署名与模型

应用代码和 Codex skill 使用 [MIT 许可](LICENSE)。模型权重、Ollama 及 Windows 组件属于独立依赖，具体说明见 [第三方组件](THIRD_PARTY_NOTICES.md)。

- 作者：[@HanJaKKK，X / Twitter](https://x.com/HanJaKKK)。
- 翻译模型：[腾讯 Hy-MT2-1.8B GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)，Q4_K_M。官方模型卡标注 Apache-2.0；仓库不附带模型权重。
- 本地推理：[Ollama](https://github.com/ollama/ollama)。
- 图像文字识别：[Windows Media OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)。

为兼容已有安装，本机模型别名继续使用 `swipetranslate-hymt2`。脚本校验官方 GGUF 的 SHA256：

```text
dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
```
