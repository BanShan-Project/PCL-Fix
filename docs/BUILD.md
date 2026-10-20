# 构建说明

本仓库产出两个互相独立的产物。两者都必须在 **Windows** 上构建；`.NET Framework 4.8` 桌面程序无法在 Linux 上编译。

## 环境

| 组件 | 用途 |
|---|---|
| .NET SDK 8.0 | 编译补丁器 `tools/RestorePcl`（net8.0） |
| .NET SDK 9.0 | 还原 `PCLCS` / `MeloongCore`（SDK 风格项目） |
| VS 2022 或 Build Tools + .NET Framework 4.8 目标包 | MSBuild 编译 VB/WPF 主程序 |
| Python 3.12 + `dnfile==0.18.0`、`dncil==1.0.2` | 读取内嵌程序集、校验输入与 IL |
| Windows PowerShell 5.1（`powershell.exe`） | `jitcheck.ps1`：.NET Framework 程序集只能在 5.1 里加载并 JIT |

## 产物一：`PCL-2.99-restored`（对官方 EXE 打补丁）

```powershell
python tools/prepare_inputs.py
dotnet build tools/RestorePcl/RestorePcl.csproj --configuration Release
New-Item -ItemType Directory -Force artifacts | Out-Null
dotnet tools/RestorePcl/bin/Release/net8.0/RestorePcl.dll `
    build-inputs/donor-2.14.exe build-inputs/original.exe artifacts/PCL-2.99-restored.exe
```

- `prepare_inputs.py` 需要联网：官方 2.99 取自本仓库的 `最新正式版.zip`，2.14 供体取自上游提交 `5ef81e4`。两者 SHA-256 都被写死在 `Program.cs` / `prepare_inputs.py` 里，不匹配立即失败，因此**它只对这两个特定二进制有效**。
- 同时会把原程序内嵌的 14 个托管依赖解到 `build-inputs/`，供 Cecil 解析类型。

验证与打包：

```powershell
python tools/verification/verify_binaries.py --original build-inputs/original.exe `
    --patched artifacts/PCL-2.99-restored.exe --ilverify <ILVerify.exe> --output verification
powershell -File tools/verification/jitcheck.ps1 -Executable artifacts/PCL-2.99-restored.exe `
    -PatchReport artifacts/PCL-2.99-restored.exe.patch.json -OutputDirectory jitcheck
```

- `verify_binaries.py`：ILVerify 基线对比（同一引用集、拒绝新增错误、拒绝被改方法报错）。
- `jitcheck.ps1`：让 CLR 逐方法编译 `patch.json` 里列出的方法。**这一步不能省**：ILVerify 不检查方法头栈上限、也不检查"try 块不得落入处理器"这类导入器规则，2.99 恢复版曾因此发出一个"验证通过、一调用就 `InvalidProgramException`"的整合包安装入口。

## 产物二：`PCL-Snapshot-restored-source`（源码编译）

```powershell
git submodule update --init --recursive      # 必须：MeloongCore
python tools/prepare_source.py               # 可选：从环境变量注入 ClientID / CurseForgeKey
dotnet restore PCLCS/PCLCS.csproj
msbuild "Plain Craft Launcher 2/Plain Craft Launcher 2.vbproj" `
    /p:Configuration=Snapshot /p:Platform=AnyCPU /m /nologo /verbosity:minimal
```

- `Configuration=Snapshot` 会定义 `SNAPSHOT` 常量，主程序里 `RestrictMcDownload = False`（`Modules\ModSecret.vb`）随之生效，下载/整合包入口走正常逻辑。
- 产物是 `Plain Craft Launcher 2.exe`：VB 项目先把编译输出写进 `obj\Snapshot\`，再复制到 `bin\`。GitHub Actions 取的是 `obj\Snapshot\` 下那一份。
- `Resources.resx` 用 `ResXFileRef` 直接引用 `..\..\PCLCS\bin\*.dll`，所以必须先构建 `PCLCS`（`ProjectReference` 会保证顺序并把这些 DLL 复制到 `PCLCS\bin\`）。

## GitHub Actions

`.github/workflows/build.yml` 有两个 job：`restored`（补丁 + ILVerify + JIT + 冒烟）与 `snapshot`（源码构建）。产物名分别是 `PCL-2.99-restored`、`PCL-Snapshot-restored-source`，验证结果打在 `restoration-verification` 里（含 `verification/`、`jitcheck/`、`smoke-results/`）。

`snapshot` 可选注入两个仓库 Secret：`CLIENT_ID`（微软应用 ID）、`CURSEFORGE_API_KEY`。不配置也能编译，只是对应联网功能会退化。

## 已知限制

- 补丁产物依赖硬编码的元数据 token 与 IL 偏移，只对 `6b6e1260…` 这个官方 2.99 二进制成立；上游发新版必须重做锚点分析，不能直接套用。
- 源码产物缺少公开源码里已被删掉的部分（CDN 签名、微软 ClientId、CurseForge Key），登录与部分下载源的可用性低于官方版。
- 两个产物都不做真实下载测试：Actions 能证明的是"IL 结构合法 + CLR 接受 + 主窗口能起来"，不能替代人工装一次整合包、启动一次游戏。
