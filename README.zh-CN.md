# 巴别塔 Babel Tower

**巴别塔 v0.2.0 就是在现有“本地增强版”上继续完善的版本。** 保留划选读取、蓝色选区本地识别和原位译文，同时加入右侧译窗、网页一键翻译，以及输入框旁的小按钮“译”。

巴别塔是一款 Windows 桌面工具，也是一份可复用的 Codex skill。它通过本机 Ollama 运行腾讯 Hy-MT2-1.8B。划选翻译是视觉覆盖，来源软件中的原文继续保留；主动点击输入框旁的“译”时，会把整个草稿换成译文，保持未发送状态。

作者：**[@HanJaKKK](https://x.com/HanJaKKK)**。 [English README](README.md)

![巴别塔设置窗口渲染预览](assets/settings-preview.png)

![巴别塔右侧译窗渲染预览](assets/right-panel-preview.png)

以上是正式控件配合合成示例文字的离屏渲染，不是用户正在使用其他软件的截图。验证范围见 [验证说明](VALIDATION.md)。

## 功能

- 加入当前 Hy-MT2 模型表中的 38 个语言选项，原文还可选“自动识别”。保留英文 → 中文和中文 → 英文快捷设置，保存后用于下一次请求。
- 优先通过 Windows 文字接口读取真正选中的文字。
- 文字接口无法提供选区时，对符合条件的蓝色选区进行本地 OCR，匹配已安装的识别语言；无法确认的选区会放弃。
- 译文能放下时显示在原选区，放不下时通过附近的浮窗显示完整内容。
- 点击别处或按 **Esc** 收起。托盘菜单可以暂停翻译或打开设置。
- 翻译完成后再次确认选区；选区变化或无法确认时，放弃这次覆盖。
- 右侧译窗保留划选原文和译文；另有“输入翻译”页签，后续划选不会覆盖手动输入的草稿。
- 符合条件的 Windows 原生输入框旁显示小按钮“译”。点击后翻译整个草稿，确认内容和输入框仍一致才写回，不会发送。
- 附带 Chrome / Edge 扩展，提供网页一键翻译、还原原文及网页输入框旁的“译”。需要手动加载扩展并连接本机应用。

发布的应用使用本机 Ollama，不读取剪贴板，不把划选文字或图片发送到云端翻译，也不会在出错后自动换成在线服务。首次下载模型及 Ollama 的安装、更新需要联网；准备完成后的翻译和识别在本机运行。

## 运行条件

- Windows 10/11，64 位。
- Windows PowerShell 5.1 或更新版本，以及 .NET Framework 4.8。
- 已安装 [Windows 版 Ollama](https://ollama.com/download/windows)，准备模型和启动应用前先打开，让本地服务保持运行。
- 为约 **1.1 GB** 的模型文件及 Ollama 导入后的模型副本留出空间。
- 图像识别后备需要对应的 Windows OCR 语言包。选择翻译语言不会自动安装识别语言；通过文字接口读取选区不需要 OCR 语言包。缺少匹配语言时会提示，不会换用其他语言的识别器。
- 网页功能需要 Chrome 或 Edge。附带的 Manifest V3 扩展以文件夹形式提供，不会自行安装进浏览器。

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

新版设置窗口标题为 **“巴别塔 · 本地增强版 0.2.0”**。点击英文 / 中文快捷方向，或从菜单选择原文和译文语言，再点击 **“保存并生效”**。下一次请求使用新的方向。同一窗口还可开启右侧译窗、输入框按钮和浏览器连接。关闭设置后工具留在托盘，双击图标重新打开。启动命令添加 `-Background` 可直接进入托盘；应用不会自行设置开机启动。

从已运行的增强版升级时，先从托盘退出旧实例，再启动新版。单实例限制会防止两份程序同时处理划选。模型别名保持不变，已有校验通过的权重可继续使用。

只检查已有构建、正在运行的 Ollama 和已校验的模型权重，不启动应用或 Ollama：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\start.ps1 -CheckOnly
```

## 右侧译窗和电脑输入框

勾选 **“划选翻译同时保留在右侧译窗”** 并保存，或点击 **“打开右侧译窗”**。“划选译文”页签显示原文和译文，“输入翻译”是独立草稿区。窗口里可修改语言并“保存语言”，也可置顶或“靠右”。关闭只收起窗口，托盘菜单能重新打开。

勾选 **“可编辑输入框旁显示「译」按钮”** 并保存。在支持的输入框中写完句子，点击旁边的“译”，整个草稿会换为目标语言，不按回车、不点击发送。翻译期间修改草稿或改变焦点，会放弃写入。

电脑端按钮先支持可写、非密码的 Windows 原生 `Edit` 输入控件，并要求其文字接口允许安全读写。其他应用、聊天客户端或特殊编辑器可能不出现按钮；网页输入框使用下面的浏览器扩展。

## 连接 Chrome / Edge，翻译网页和网页输入

1. 运行巴别塔，在设置中勾选 **“浏览器网页翻译连接（需要扩展）”** 并保存，打开 **“网页翻译 · 连接浏览器”**。窗口可打开随程序附带的 `browser-extension` 文件夹，并显示这份安装的连接码。
2. 浏览器打开 `chrome://extensions` 或 `edge://extensions`，开启 **“开发者模式”**，选择 **“加载已解压的扩展程序”**，指定 `browser-extension` 文件夹。程序不会替用户改浏览器配置或静默安装扩展。
3. 打开扩展的 **“连接设置与使用说明”**，填入连接码，点击 **“保存并检查连接”**，状态应显示 **“已连接本机巴别塔”**。扩展加载前已打开的普通网页，请刷新后使用。
4. 点击浏览器工具栏里的巴别塔，再点击 **“一键翻译当前网页”**。可用 **“停止翻译”** 中断任务，或用 **“还原原文”** 恢复网页未再次改动的文字。
5. 在支持的文字输入框、多行输入框或纯文本可编辑区域写完句子，点击旁边的小“译”。译文写回当前输入框，仍未发送。密码、验证码、受保护内容和不支持的复杂编辑器会跳过。

扩展使用小窗口保存的翻译方向，在扩展菜单中 **“保存翻译方向”** 也会同步到本机应用。网页翻译只在点击后读取符合条件的正文，跳过输入框和代码，原文暂存在内存中用于还原。长网页可能需要等待，部分内容可能跳过；后来加载的新文字需要再次点击翻译。浏览器内部页面、扩展商店、PDF、内嵌框（iframe）和图片文字暂不支持。

浏览器扩展的网页或输入翻译，每次请求最多等待 **25 秒**；超时保留尚未替换的原文，待模型预热或缩短输入文字后重试。**“停止翻译”** 会阻止写回和后续节点处理，但已经提交的模型计算可能继续一段时间。

浏览器连接固定使用 `http://127.0.0.1:17863`。每份安装生成一个 64 字符十六进制连接码，保存在程序旁的 `browser-bridge.json`；这个本机文件不应加入发布包或公开仓库。扩展把连接码存在受信任扩展上下文的本地设置中，不放入网页内容。文字经过本机连接送给同一本地模型处理。详细说明见 [浏览器扩展说明](browser-extension/README.md)。

部分 Windows 配置会拒绝本地监听权限，或端口被占用。连接窗口会显示失败原因，划选和右侧译窗仍可使用。程序不会自行提权或修改系统 URL 访问权限；这类连接失败与模型能否翻译是不同问题。

## 更多语言与识别语言

菜单包括简体中文、繁体中文、英文、日文、韩文、法文、德文、西班牙文、俄文、葡萄牙文、土耳其文、阿拉伯文、泰文、意大利文、越南文、马来文、印尼文、菲律宾文、印地文、波兰文、捷克文、荷兰文、高棉文、缅甸文、波斯文、古吉拉特文、乌尔都文、泰卢固文、马拉地文、希伯来文、孟加拉文、泰米尔文、乌克兰文、藏文、哈萨克文、蒙古文、维吾尔文和粤语。这是模型语言选项，不是逐项翻译精度认证。

需要 OCR 时，尽量明确选择原文语言。“自动识别”对图像识别仍使用英 / 中方向作为提示，无法仅由所有目标语言推断对应识别器。缺少匹配的 Windows OCR 语言时会明确提示，不会自动换用另一种语言。

## 安装成 Codex skill

整个仓库就是 skill 文件夹。将其克隆到自己的 Codex skills 目录，文件夹命名为 `babel-tower`。默认 Windows 配置可以使用：

```powershell
$babelSkillDirectory = Join-Path $env:USERPROFILE '.codex\skills\babel-tower'
git clone https://github.com/roushan11111/babel-tower.git $babelSkillDirectory
```

如果设置了自己的 `CODEX_HOME`，放到该目录的 `skills` 子目录中。刷新 skill 列表或重新启动 Codex，然后可以说：

```text
使用 $babel-tower，在这台 Windows 电脑上配置本地增强版，并连接网页和输入框翻译。
```

这份 skill 用于构建、启动、配置和排查巴别塔。只安装 skill 不会同时安装 Ollama、模型文件或浏览器扩展。

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
| `browser-extension/` | 可选 Chrome / Edge 扩展 |
| `scripts/build.ps1` | 编译应用 |
| `scripts/start.ps1` | 检查条件并启动 |
| `scripts/setup-model.ps1` | 明确下载模型或校验导入已有文件 |
| `dist/BabelTower.exe` | 本地构建结果及发布附件 |

## 兼容范围与验证情况

能否读取选区取决于来源软件。提供 Windows 文字选区接口的浏览器和编辑器通常更适合。自绘画布、游戏界面、受保护输入、非蓝色高亮、文字被遮挡的区域及部分管理员权限应用可能不支持。本地 OCR 可能拒绝不清晰或不完整的选区，小字号及部分中文字形仍可能被错识别。翻译不能保证专业术语、语义和标点完全准确。

现有增强版的原位中文显示曾在记事本测试中被观察到，用户也在自己的 GPT 窗口确认了覆盖。v0.2.0 已通过设置、右侧译窗、电脑输入控制器、本机连接服务及受控浏览器扩展检查。新融合版本的真实桌面完整流程和用户 GPT 网页流程仍未手动实测；不能用旧版观察或受控示例代替这些验证。具体记录见 [验证说明](VALIDATION.md)。

没有出现译文时，先等待首次模型预热，再在普通文字控件中完整划选一行英文。托盘里的“查看运行状态”会显示阶段和划选次数，不记录选区原文。翻译期间选区变化时，重新划选即可。出现浮窗可能是译文放不进原选区，或原位覆盖已关闭。

## 署名与模型

应用、浏览器扩展和 Codex skill 使用 [MIT 许可](LICENSE)。模型权重、Ollama 及 Windows 组件属于独立依赖，具体说明见 [第三方组件](THIRD_PARTY_NOTICES.md)。

- 作者：[@HanJaKKK，X / Twitter](https://x.com/HanJaKKK)。
- 翻译模型：[腾讯 Hy-MT2-1.8B GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF)，Q4_K_M。官方模型卡标注 Apache-2.0；仓库不附带模型权重。
- 本地推理：[Ollama](https://github.com/ollama/ollama)。
- 图像文字识别：[Windows Media OCR](https://learn.microsoft.com/en-us/uwp/api/windows.media.ocr.ocrengine)。

为兼容已有安装，本机模型别名继续使用 `swipetranslate-hymt2`。脚本校验官方 GGUF 的 SHA256：

```text
dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699
```
