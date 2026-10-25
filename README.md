# PCL2 RegionPatch

以热修补的方式解除 `ModSecret.RestrictMcDownload` 对 MC 下载的限制。

仅供学习参考，请于24h内删除，本项目与PCL2没有任何附属关系。

> [HMCL版本 - HMCL RegionPatch](https://github.com/zhicheng233/HMCL-RegionPatch)

## 警告

在使用前请确保相关dll源自 `Release` 下载或本仓库源码编译产物，PCL2为官方渠道下载。必要时核验SHA256。

因使用任何 **未知来源** 的二进制文件导致的任何问题请自行承担后果。

## 目录结构

```
PCL2-RegionPatch\
├─ payload\                              部署用的 3 个文件（安装时复制到 PCL2 目录）
│  ├─ Plain Craft Launcher 2.exe.config  注入入口 + 长路径修复
│  ├─ PclRuntime.dll                     AppDomainManager
│  └─ 0Harmony.dll                       Lib.Harmony 2.3.3
├─ src\                                  源码
├─ ci\                                   CI 辅助脚本（产物校验、生成 Release 正文）
├─ .github\workflows\ci.yml              GitHub Actions 工作流
├─ build.ps1                             构建脚本
├─ install.ps1                           安装脚本
└─ uninstall.ps1                         卸载脚本
```

## 安装
>接下来我们默认被修补的PCL2启动器的EXE名称为 **`Plain Craft Launcher 2.exe`**，如果你的PCL2启动器为其他名称请在安装后手动修改`.config`文件为 `你的PCL2启动文件名.config` (注意带.exe)

### 1. 脚本安装

把整个 `PCL2-RegionPatch` 文件夹放到 PCL 目录里，然后：

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

或者从任意位置指定目标：

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Target "D:\Games\PCL2"
```

脚本会校验目标目录里有 `Plain Craft Launcher 2.exe`，然后复制 3 个文件。
如果目录里已存在**别的** `.exe.config`，会先备份成 `.exe.config.bak`。

### 2. 手动安装
将 `0Harmony.dll`、`PclRuntime.dll` 和 `Plain Craft Launcher 2.exe.config`，放入`Plain Craft Launcher 2.exe`所在的文件夹

## 验证

启动 PCL，然后看 `%TEMP%\PclRuntime.log`，正常应该是：

```
[3] PatchHost started
[3] [1] patched PCL.PageDownloadInstall.LoaderInit (06000513)
[3] [2] patched PCL.PageDownloadModify.LoadInstanceFinished (06000652)
[3] [3] patched PCL.PageDownloadResourceDetail.InitializeStackResourceVersions (0600068B)
[3] [4] patched PCL.PageLaunchLeft._Lambda$__4-0 (06000C0F)
[3] [4] patched PCL.ModModpack+_Closure$__0-0._Lambda$__0 (060012EE)
[3] [4] patched PCL.ModModpack+_Closure$__10-0._Lambda$__0 (060012F0)
[3] [4] patched PCL.PageDownloadResourceDetail+_Closure$__22-0._Lambda$__0 (060014B8)
[3] [4] patched PCL.FormMain+_Closure$__25-0._Lambda$__0 (060017FF)
[3] [5] patched PCL.ModModpack.ModpackInstall (060001EF)
[3] PatchHost: done
```

功能上：下载页能正常使用

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

或者手动删掉那 3 个文件即可（有 `.bak` 会自动还原）。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

需要 .NET SDK（`net48`）。

## 环境要求

- Windows + .NET Framework 4.8
- PCL 2.99.0.0（SHA-256 `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386`）

## 它是怎么生效的

1. CLR 读 `Plain Craft Launcher 2.exe.config`，在 `Main` 执行**之前**实例化
   `PclRuntime.PclRuntimeManager`（`AppDomainManager` 子类）。
2. 此时 `Assembly.GetEntryAssembly()` 还是 `null`；后台线程先等待入口程序集，再监听 `PresentationFramework` 加载并挂钩 `Window.Loaded`，确保在 UI 真正启动后再打补丁。
3. 用 Harmony 给 5 个目标一次性打补丁，打完线程退出。

> 由于PCL2 2.99 的解决方案是临时引入 `ModSecret.RestrictMcDownload` 常量来控制具体流程；导致其应用在编译时，相关逻辑代码被编译器内联直接优化掉了，因为 `RestrictMcDownload` 恒为 `True` 所以正常逻辑永远不可达，然后编译器就把这玩意删掉了，是的没错，有些功能压根就不在最终的CIL里，这东西可以通过 `ILSpy` 对PCL2的反编译结果看出。<br>
所以最终的解决方法是对整个PCL2在内存上进行修补，而不是简单将某个寄存器翻转或者更改某个`jmp`的地址。

## 调试开关

| 环境变量 | 作用 |
|---|---|
| `PCLRT_NOPATCH=1` | 只注入不打补丁（判断问题是否来自补丁） |
| `PCLRT_ONLY=1,3` | 只打指定编号的补丁 |
| `PCLRT_TRACE=1` | 记录每个首次异常的类型 + 抛出点调用栈 |
| `PCLRT_ID3=1` | 补丁 3 改为恒等透传（验证 transpiler 本身） |

## 排查

| 现象 | 原因 / 处理 |
|---|---|
| 启动弹"没有对当前文件夹的权限" | config 少了 `AppContextSwitchOverrides` 那行 |
| 完全没反应、没有日志 | config 文件名不对（必须是 `<exe 全名>.config`），或 DLL 不在 exe 同目录 |
| 日志只有 `PatchHost started` | PCL2 启动过程中被提前关闭，导致尚未等到 `Window.Loaded` |
| 日志有 `patch failed: ...` | PCL2 版本变了，方法名/签名对不上，需要按 `README-Patch.md` 重新定位 |


## 许可证
该程序在 GPLv3 开源协议下发布