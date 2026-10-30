# 动画触发矩阵取证（脚本自动驱动）
- 时间：2026-10-31 01:17:21
- 进程：D:\Beautiful-main\dist\Beautiful\Beautiful.exe

## 挂载自检（启动期日志）
```
[2026-10-31 01:17:02.045] [INFO] 动画参数：已加载 genie=500ms split=0.30 restore=450ms morph=240ms settle=180ms open=200ms/0.94 close=150ms/0.92 缓存=700ms/500ms 风暴=4/200ms 倍率=1.0 引擎=mesh
```
```
[2026-10-31 01:17:02.052] [INFO] 动画参数：已监听 D:\Beautiful-main\dist\Beautiful\config\anim-params.json（改文件即热加载）
```
```
[2026-10-31 01:17:02.056] [INFO] 动画子系统已挂载：MINIMIZE=0x5DA0763 LOCATION=0xAA80879 设置=开 请求引擎=mesh 生效引擎=mesh 前台缓存=开 参数[genie=500ms split=0.30 restore=450ms morph=240ms settle=180ms open=200ms/0.94 close=150ms/0.92 缓存=700ms/500ms 风暴=4/200ms 倍率=1.0 引擎=mesh]
```
```
[2026-10-31 01:17:02.062] [WARN] 系统动画接管：本机系统不响应 SPI_GETANIMATION/SPI_SETANIMATION（新系统限制，探针 tools/spi-probe 可复现），本次不接管——系统「缩向任务栏」动画可能与 genie 同时播放，属已知限制
```

## 矩阵
| 触发路径 | 证据行数 | 判定 |
|---|---|---|
| caption按钮(WM_SYSCOMMAND) | 1 | 已触发 |
| 任务栏路径(SW_MINIMIZE) | 2 | 已触发 |
| Win+Down(热键) | 0 | 未播（见证据） |
| Dock点击(UIA) | -1 | 脚本不可驱动 |

## 触发证据行
- [2026-10-31 01:17:14.559] [INFO] 动画触发：源=MINIMIZEEND 层=屏幕 引擎=mesh 时长=450ms 帧数=13 P95=80.0ms
- [2026-10-31 01:17:16.431] [INFO] 动画触发：源=MINIMIZEEND 层=屏幕 引擎=mesh 时长=450ms 帧数=17 P95=127.4ms

## 未播证据行
- [2026-10-31 01:17:07.558] [INFO] 动画未播：源=MINIMIZESTART 层=缓存 因=到手时已最小化且缓存帧过期/缺失 细节=缓存条数=1
- [2026-10-31 01:17:14.553] [INFO] 动画未播：源=MINIMIZESTART 层=无 因=上一条动画未结束（单替身闸门）

## 人工步骤清单（脚本驱动不了的部分）
1. 拖动窗口到副屏后再最小化（混合 DPI 落点核对）。
2. 全屏播放视频期间最小化（应整段不播且 Dock/菜单栏隐藏）。
3. 4 个窗口 200ms 内连续最小化（应触发风暴抑制，日志出现"因=风暴抑制"）。
