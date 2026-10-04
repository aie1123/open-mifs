# 仓库元信息（About / Topics / 发布清单）

> 用途：GitHub 仓库页「About」栏的文案、Topics 标签，以及每次发版的核对清单。
> 改功能后如果定位描述变了，**回来更新这里和 README 首段**。

---

## 1. About → Description（一句话，GitHub 上限 350 字符）

**推荐（116 字符，中文）：**

```
同方/机械革命笔记本的 MIFS 控制台：单文件 exe、零依赖免驱动，控制性能模式与硬件开关，并显示 CPU/GPU 温度等传感器
```

**英文备选（供双语仓库或搜索）：**

```
Zero-dependency single-file console for TongFang/MECHREVO MIFS laptops: performance modes, hardware switches, and driver-free CPU/GPU temperature monitoring
```

**更短（60 字符，适合窄栏）：**

```
同方 MIFS 控制台：单文件 exe，零依赖免驱动，带传感器面板
```

选择建议：**用推荐那条**。它同时命中三类搜索意图 ——
机型（同方/机械革命）、形态（单文件 exe / 零依赖 / 免驱动）、功能（性能模式 / 硬件开关 / 温度）。

---

## 2. Topics（GitHub 上限 20 个，建议 8~10 个）

按"先机型、再形态、后功能"排：

```
tongfang  mechrevo  mifs  acpi-wmi  laptop-control  fan-speed  hardware-monitor
windows  csharp  powershell  zero-dependency  no-driver  amd-ryzen
```

推荐保留前 10 个：`tongfang`、`mechrevo`、`mifs`、`acpi-wmi`、`laptop-control`、
`hardware-monitor`、`windows`、`csharp`、`zero-dependency`、`no-driver`

---

## 3. Website 字段（可选）

留空即可（没有官网）。若想填，用 Release 页最稳：

```
https://github.com/aie1123/open-mifs/releases/latest
```

---

## 4. Social preview（仓库设置里手动上传，1200×630）

用 `docs/screenshots/gui.png` 或重新截一张**提权态**主界面（左侧控件不置灰、传感器 16 项全绿）。
建议在图右上角叠一行：`零依赖 · 免驱动 · 单文件 exe`。

---

## 5. 发版核对清单

每次 `git tag vX.Y.Z` 前过一遍：

| # | 检查项 | 命令/位置 |
| :---: | :--- | :--- |
| 1 | 构建通过 | `powershell -File build\build.ps1` → 看 `OPENMIFS_BUILD_OK` |
| 2 | 版本号已改 | `src/csharp/OpenMIFS.cs` 的 `AssemblyVersion` / `AssemblyFileVersion` |
| 3 | 真机验证过 | 用 AGENTS.md §5 的 asInvoker 副本跑一遍，**把数字写进 CHANGELOG 的"验证"小节** |
| 4 | CHANGELOG 已写 | 新增/修复/验证三段齐全 |
| 5 | 文档同步 | README 功能表、docs/ 里对应那份、本文件的 About 描述 |
| 6 | CLI 一致性 | `mifs.ps1` 的 `switch` 与 `ValidateSet` 两处都有（CI 会拦） |
| 7 | 提交推送 | `git push origin main` + `git push origin vX.Y.Z`（本机需代理 `127.0.0.1:7890`） |
| 8 | 产物验证 | 下载 Release 里的 exe，核对**大小**与关键字符串（哈希每次不同，正常） |
| 9 | CI 绿 | Actions 里 `build` 通过 |

---

## 6. 仓库描述里不要写的

- ❌ "支持所有同方机型" —— 功能号含义因机型而异，必须靠 `probe` 确认
- ❌ "可以调节风扇转速" —— 本机实测不可用（电源类型门控），只在部分机型可能可用
- ❌ "能读 Tctl / 主板 / VRM 温度" —— 需要内核驱动，本项目刻意不做
- ❌ 任何"官方"字样 —— 与机械革命/同方无关联（README 免责声明已写明）
