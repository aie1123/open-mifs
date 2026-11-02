# UI 自适应布局与验证（2026-11-02）

## 原因与取舍

旧版以 `min(窗口宽 / 940, 窗口高 / 860)` 计算整张画布的缩放，随后仍以固定坐标摆放控件。
这同时混合了窗口大小、DPI、字体行高和内容量：字体缩小不保证中文或原生下拉框等比缩小，
偏好设置又固定高度且关闭滚动，右侧底部说明也没有计入明细实际高度。

新版继续使用 C# 5 / .NET Framework / WinForms、现有系统 DPI 感知策略和零依赖单文件产物。
主要布局由 `MainForm.Layout.cs` 管理；`OpenMIFS.cs` 保留事件、硬件读写和托盘生命周期，`Ui.cs` 管理字体与可重排读数。

## 布局规则

1. 字号和间距只取决于 DPI，不随窗口尺寸缩小。字体以像素单位生成，避免点字号再次乘系统 DPI。
2. 关键读数横跨内容宽度；宽度足够时控制 / 明细双列，窄窗口改为单列。偏好设置单独跨列展开。
3. 文字测量决定说明、按钮、读数和设置区域的实际高度；较长的数值与单位可分行。
4. 全部内容使用同一个纵向滚动视口并预留滚动条宽度，不生成横向滚动。
5. 小窗口或大字号令头尾也进入滚动内容，避免固定栏挤没主区域。所有操作仍可滚动访问。

## 可复现命令

在仓库根目录运行；测试自动编译 `asInvoker` 临时副本，使用独立配置目录和互斥体，结束后清理。

```powershell
powershell -ExecutionPolicy Bypass -File build\test-layout.ps1
powershell -ExecutionPolicy Bypass -File build\test-layout.ps1 -ToggleTest
powershell -ExecutionPolicy Bypass -File build\build.ps1
powershell -ExecutionPolicy Bypass -File build\test-startup.ps1
```

CI 可用 `-NoScreenshots` 跳过 DWM 屏幕截图；它仍运行真实 WinForms 控件测量。
测试临时放宽窗口最大尺寸，通过原生 `SetWindowPos` 设置窗口尺寸并断言实际客户区与请求值一致，避免 CI 小桌面把 1200 / 1500 像素场景截成屏幕大小。
最大化检查前恢复默认最大尺寸约束，还原后再次放宽，以继续检查超出 CI 桌面大小的窗口。
另检查大于当前桌面最大跟踪尺寸的布局；不修改系统分辨率或生产代码。
几何测试把传感器刷新在临时副本中隔离，并明确标识排版样本；`-ToggleTest` 则保留真实刷新与托盘路径。

## 本机证据

- 旧界面：`test-output/ui-refactor-20261102/before/failures.txt`，13 项失败；720×600 截图显示底部设置被裁切。
- 新界面：`test-output/ui-refactor-20261102/after/results.txt`，144 个场景，失败 0。
- 三次实际托盘切换：`test-output/ui-refactor-20261102/toggle/runtime.log`，包含“全部完成，未崩溃”，可见态均在屏幕内。
- 截图使用 `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9)` 与 `CopyFromScreen`，分别保存顶部与滚动到底部。

## 验证边界

DPI 矩阵是应用布局尺度模拟，没有更改本机系统缩放，也没有执行多显示器真实 DPI 迁移。
当前窗体为手工构建，没有 `.Designer.cs` / `.resx`；未执行 Visual Studio Designer 往返保存。
2026-11-02，作者完成本地测试并确认“测试没什么问题”，明确授权同步远端并发布 v0.6.5。
作者未单独说明跨屏 DPI 迁移的测试结果，因此仍保留上述验证边界。

## 使用的指导

- `ui-ux-pro-max`：关键文字完整可达、内容决定高度、单一滚动区及响应式换列。
- `.NET dotnet/skills` 的 `winforms-expert`：逐层检查父容器增长限制，保持框架与 DPI 策略，用实际布局检查而非仅构建判断结果。
