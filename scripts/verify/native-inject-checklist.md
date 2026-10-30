# Native 注入演练核对清单（v4 Phase 2，所有者真机）

> 只读纪律：TrayBridge 空 payload 只创建心跳/卸载事件，不 hook、不读写 explorer 数据。
> 失败/权限不足就停止，不重试、不提权。

## 前提

- 已运行 `scripts\build-native.cmd`，`Beautiful.Native.dll` 与 `Beautiful.TrayBridge.dll` 在 `dist\Beautiful\`。
- 设置页「底层增强」显示 `Native 已加载`；「TrayBridge 注入」勾选。
- 当前 Windows build 在 22000..26999（NativeSmoke 的 `inject OS-build gate` 为 PASS）。

## 步骤

1. 打开设置 → 底层增强 → 点「注入演练…」→ 二次确认。
2. 期望日志（`logs\native.log`）：`InjectExplorer: 注入成功`；`%TEMP%\Beautiful.TrayBridge.log`：`TrayBridge 已装载（空 payload：仅心跳/卸载）` 与 `收到卸载命令`。
3. 观察 explorer：不重启、桌面不闪、任务栏/Dock 无异常。
4. 若注入失败：记录 code 与 `Bn_LastError`，确认杀软/权限；**不要**为此上管理员提权。
5. 若失败后 explorer 有异常：立即 `--exit` 退出 Beautiful，回报日志；不要反复演练。

## 回执

把 `logs\native.log`、`%TEMP%\Beautiful.TrayBridge.log`、`scripts\diag.ps1` 输出发回，
标记 `VERIFIED(owner, 日期)`；未回执前一律 UNVERIFIED。
