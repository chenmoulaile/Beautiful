# 第三方声明（THIRD-PARTY NOTICES）

Beautiful 以 MIT 许可证发布（见 [LICENSE](LICENSE)）。本项目为独立编写的原创代码，但在设计过程中参考了下列开源项目的公开实现模式、设计资源与数据接口，特此逐项声明与致谢。

代码中参考外部项目实现模式之处以注释就地标注（格式如 `// 实现模式参考 <repo> (<license>) ...`），例如 `src/Beautiful.App/Interop/NativeMethods.cs`、`src/Beautiful.App/Services/AcrylicWindow.cs`、`src/Beautiful.App/Switcher/AltTabHook.cs`（cmdtab 拦截机制标注）、`src/Beautiful.Core/Lyrics/NetEaseProvider.cs`（端点用法标注）；`src/Beautiful.Core/Lyrics/LrcLibProvider.cs` 的对应注释为数据来源说明（lrclib.net）。

---

## 1. MIT 许可的开源项目

本项目未复制以下任何项目的代码，仅借鉴其公开文档/实现思路：

| 项目 | 许可证 | 参考内容 | 对应代码位置 |
| --- | --- | --- | --- |
| [TheAgencyMGE/aero-dock](https://github.com/TheAgencyMGE/aero-dock) | MIT | 悬浮 Dock 实现模式参考（未复制代码） | Dock 悬浮布局设计 |
| [ModernFlyouts-Community/ModernFlyouts](https://github.com/ModernFlyouts-Community/ModernFlyouts) | MIT | GSMTC（SMTC）媒体会话管理模式；`SetWindowCompositionAttribute` 亚克力/毛玻璃用法 | `src/Beautiful.App/Services/SmtcService.cs`、`src/Beautiful.App/Services/AcrylicWindow.cs`、`src/Beautiful.App/Interop/NativeMethods.cs` |
| [wwh1004/NeteaseCloudMusicApi](https://github.com/wwh1004/NeteaseCloudMusicApi) | MIT | 网易云歌词接口（含翻译 `tlyric`）端点用法文档 | `src/Beautiful.Core/Lyrics/NetEaseProvider.cs` |
| [tranxuanthang/lrclib](https://github.com/tranxuanthang/lrclib) | MIT | LRCLIB 开放歌词 API 协议 | `src/Beautiful.Core/Lyrics/LrcLibProvider.cs` |
| [stianhoiland/cmdtab](https://github.com/stianhoiland/cmdtab) | MIT | Alt+Tab 拦截机制参考（低阶键盘钩子的接管/放行思路；实现为 C# 独立编写，未复制其代码） | `src/Beautiful.App/Switcher/AltTabHook.cs`（头部就地标注） |

MIT 许可证原文（各项目的版权与许可声明以其仓库内 LICENSE 文件为准）：

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## 2. Apache-2.0 许可的设计资源

| 项目 | 许可证 | 用途 |
| --- | --- | --- |
| [dososo/HarmonyOS-Design](https://github.com/dososo/HarmonyOS-Design) | Apache-2.0 | 鸿蒙主题的设计 token（配色/圆角/间距规范参考），对应 `src/Beautiful.App/Themes/HarmonyOS.xaml` 与 `src/Beautiful.Core/Themes/Superellipse.cs` |

未复制其代码与美术资产，仅参考公开的设计规范数值。Apache License 2.0 全文见该仓库 LICENSE 文件。

## 3. 仅借鉴思路的 GPL/AGPL 项目（未复制代码）

以下 GPL/AGPL 项目仅作为行为参照与思路来源，**本项目未复制、未衍生、未链接其任何代码**，故不构成本项目的 GPL/AGPL 传染义务；列出以示尊重与致谢（许可均以各仓库 LICENSE 文件为准）：

| 项目 | 许可证 | 借鉴内容 |
| --- | --- | --- |
| [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB) | GPL-3.0 | 任务栏透明化与 Win32 API 序列的公开用法 |
| [KDE Latte Dock](https://invent.kde.org/plasma/latte-dock) | GPL-2.0+ | Dock 布局与交互行为参照 |
| [Plank](https://github.com/ricotz/plank) | GPL-3.0 | Dock 简洁交互参照 |
| [ExplorerPatcher](https://github.com/valinet/ExplorerPatcher) | GPL-2.0 | 任务栏控制思路参照 |
| [Windhawk taskbar-styler（windows-11-taskbar-styling-guide）](https://github.com/ramensoftware/windows-11-taskbar-styling-guide) | GPL-3.0 | 任务栏样式定制思路参照 |
| [lwouis/alt-tab-macos](https://github.com/lwouis/alt-tab-macos) | GPL-3.0 | 切换器交互规格参考（应用分组/多窗口展开/按住-松开语义；未复制代码。注意：其许可证经 GitHub API 核实为 GPL-3.0，并非 MIT，故归入本节而非第 1 节） |
| [micheleg/dash-to-dock](https://github.com/micheleg/dash-to-dock) | GPL-2.0 | 悬停高斯放大公式的**思路**来源（`MagnificationField` 为本项目独立推导的纯函数） |
| [eythaann/Seelen-UI](https://github.com/eythaann/Seelen-UI) | AGPL-3.0 | 切换器/工具栏的产品形态参照（仅观看其公开演示与文档，未复制代码） |
| [ejbills/DockDoor](https://github.com/ejbills/DockDoor) | GPL-3.0（GitHub 标注为 Other/NOASSERTION，LICENSE 正文为 GPL） | Dock 悬停实时窗口预览的交互参照（未复制代码；本项目实现为 DWM 缩略图 API 的独立封装） |

## 4. 数据来源

- **网易云音乐接口**（[music.163.com](https://music.163.com/)）：非官方接口，歌词（含双语翻译）数据来源。仅限个人使用，本项目不对其进行任何商业用途；接口可用性不受本项目控制。
- **LRCLIB**（[lrclib.net](https://lrclib.net/)）：开放歌词 API，作为歌词兜底来源。

## 5. 华为专有授权字体与图标

**HarmonyOS Sans 字体**与 **HarmonyOS Symbol 图标**版权归华为所有，为华为专有授权资源，不属于任何开源许可证。

- **授权范围**：华为官方资源页（[HarmonyOS 设计资源](https://developer.huawei.com/consumer/cn/design/resource-V1/)）许可将上述字体/图标**随软件嵌入使用**，条件是保留授权声明、**不得单独分发或修改字体文件本身**。
- **本项目现状**：当前发布中**未嵌入** HarmonyOS Sans 字体文件或 HarmonyOS Symbol 图标文件，界面通过字体名回退链引用系统已安装字体（未安装时回退默认字体），视觉效果不依赖华为字体资源。
- **未来若嵌入**：须在发布物中附带华为官方资源页的授权声明，并遵守"随软件分发、不单独分发、不修改字体文件"的条款。

## 6. 商标声明

- **Apple 商标**：本项目**未使用** Apple 的商标、名称或徽标。界面中的 "macOS 风格" 仅为设计形态的描述性用语；顶栏 logo 为**自绘原创标记**（主题强调色圆底 + 字母 "B"，见 `TopBarWindow.MakeLogoButton`），不是 Apple 标志。macOS 是本片描述所涉第三方系统的名称，其商标归 Apple Inc. 所有。
- **HarmonyOS / 鸿蒙**：名称与字体/图标资源版权归华为所有，本项目仅为设计 token 的描述性引用，未嵌入华为资源（见第 5 节）。

---

如发现本清单有遗漏或错误，欢迎提 Issue 指正。
