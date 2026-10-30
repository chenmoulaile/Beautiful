# 动画触发矩阵取证（脚本自动驱动）
- 时间：2026-10-31 01:20:51
- 进程：D:\Beautiful-main\dist\Beautiful\Beautiful.exe

## 挂载自检（启动期日志）
```
[2026-10-31 01:20:20.327] [INFO] 动画参数：已加载 genie=500ms split=0.30 restore=450ms morph=240ms settle=180ms open=200ms/0.94 close=150ms/0.92 缓存=700ms/500ms 风暴=4/200ms 倍率=1.0 引擎=mesh
```
```
[2026-10-31 01:20:20.334] [INFO] 动画参数：已监听 D:\Beautiful-main\dist\Beautiful\config\anim-params.json（改文件即热加载）
```
```
[2026-10-31 01:20:20.338] [INFO] 动画子系统已挂载：MINIMIZE=0x18B0591 LOCATION=0x44007CD 设置=开 请求引擎=mesh 生效引擎=mesh 前台缓存=开 参数[genie=500ms split=0.30 restore=450ms morph=240ms settle=180ms open=200ms/0.94 close=150ms/0.92 缓存=700ms/500ms 风暴=4/200ms 倍率=1.0 引擎=mesh]
```
```
[2026-10-31 01:20:20.343] [WARN] 系统动画接管：本机系统不响应 SPI_GETANIMATION/SPI_SETANIMATION（新系统限制，探针 tools/spi-probe 可复现），本次不接管——系统「缩向任务栏」动画可能与 genie 同时播放，属已知限制
```

## 矩阵
| 触发路径 | 证据行数 | 最小化判定 |
|---|---|---|
| caption按钮(WM_SYSCOMMAND) | 1 | 最小化未播（还原/判废证据见下） |
| 任务栏路径(SW_MINIMIZE) | 1 | 已触发 |
| Win+Down(热键) | -1 | 脚本不可驱动 |
| Dock点击(UIA) | -1 | 脚本不可驱动 |

## 触发证据行
- [2026-10-31 01:20:39.133] [INFO] 动画触发：源=MINIMIZEEND 层=屏幕 引擎=mesh 时长=450ms 帧数=14 P95=78.7ms
- [2026-10-31 01:20:40.590] [INFO] 动画触发：源=MINIMIZESTART 层=缓存 引擎=mesh 时长=500ms 帧数=10 P95=106.0ms
- [2026-10-31 01:20:41.893] [INFO] 动画触发：源=MINIMIZEEND 层=屏幕 引擎=mesh 时长=450ms 帧数=16 P95=79.7ms

## 未播证据行
- [2026-10-31 01:20:32.083] [INFO] 动画未播：源=MINIMIZESTART 层=缓存 因=到手时已最小化且缓存帧过期/缺失 细节=缓存条数=1

## 人工步骤清单（脚本驱动不了的部分）
1. 拖动窗口到副屏后再最小化（混合 DPI 落点核对）。
2. 全屏播放视频期间最小化（应整段不播且 Dock/菜单栏隐藏）。
3. 4 个窗口 200ms 内连续最小化（应触发风暴抑制，日志出现"因=风暴抑制"）。
