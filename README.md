# Wihomo

基于 [mihomo](https://github.com/MetaCubeX/mihomo) 内核的 Windows 代理客户端，使用 WinUI 3（.NET 10 + Windows App SDK）构建。

## 功能

- **内核管理** — 启动/停止/重启 mihomo 内核，实时查看连接数、流量速率与累计用量
- **订阅管理** — 添加、编辑、删除订阅，支持自动解析订阅流量与到期信息
- **代理组切换** — 查看代理组及组成员，点击节点即可切换，支持全部节点延迟测速
- **设置即时生效** — 保存设置时优先热重载内核配置，仅在内核路径/控制端口等无法热切换时自动重启
- **外部资源** — 配置 GeoIP / GeoSite / MMDB 等外部数据文件的下载地址与自动更新
- **YAML 覆写** — 通过 YAML 片段自定义 mihomo 配置
- **规则与连接** — 查看当前规则列表和活跃连接
- **本地与局域网直连** — 可选让内网 IP 与 `.lan`/`.local`/`.home.arpa` 域名绕过代理，直连打印机/NAS
- **系统代理** — 启动内核时自动接管 Windows 系统代理，停止时恢复
- **开机自启** — 通过 MSIX `startupTask` 随 Windows 启动并自动运行内核
- **系统托盘** — 关闭窗口即隐藏到托盘，单击托盘图标恢复窗口

## 环境要求

- Windows 10 1809+ / Windows 11 x64
- 构建需要 .NET 10 SDK
- 安装包自包含 .NET 与 Windows App SDK 运行时，最终用户无需预装

## 构建

```bash
dotnet build -p:Platform=x64
```

### 与其他代理共存

默认实例沿用 7890/7891/7892/9090。如果本机已有代理占用这些端口，用 `WIHOMO_INSTANCE_SUFFIX` 启动一个完全独立的实例：

```powershell
$env:WIHOMO_INSTANCE_SUFFIX = "dev"
.\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Wihomo.exe
```

独立实例使用 `%AppData%\Wihomo-dev`、`%LocalAppData%\Wihomo-dev\runtime` 与 8090/8091/8092/9095，不接管开机自启，也不会和正式实例互相激活或覆盖配置。

如果需要在调试时使用当前安装版的真实设置，但不修改原设置：

```powershell
powershell -ExecutionPolicy Bypass -File Installer\start-dev-profile.ps1
```

脚本会把设置和订阅复制到 `%LocalAppData%\Wihomo\DevProfile`，改写为独立端口并关闭系统代理与开机自启，调试实例只读写这份副本。原始 `settings.json`、订阅文件和系统代理不会变化。

## 测试

```bash
dotnet test Wihomo.Tests/Wihomo.Tests.csproj -p:Platform=x64
```

测试体系包含：配置生成的黄金基线（golden master）、mihomo WebSocket 协议帧回放、视图模型单元测试。XAML 绑定使用 `x:Bind` 在编译期校验路径。集成测试会拉起内置的 mihomo 内核验证遥测链路。

集成测试通过 `GetFreeLoopbackPort()` 动态申请端口，不会占用 7890/7891/7892，因此可以和正在运行的代理客户端并行执行；需要跳过内核集成测试时设置 `WIHOMO_SKIP_INTEGRATION=1`。

> vstest 18 的 testhost 以 Low 完整性级别运行，测试临时文件统一写到构建生成的 `TestTemp`（见 `Wihomo.Tests/TestPaths.cs`），不要直接使用 `Path.GetTempPath()`。

## 打包与安装

```powershell
# 生成并签名 MSIX（首次运行会创建并信任当前用户的自签名开发证书）
powershell -ExecutionPolicy Bypass -File Installer\build-msix.ps1

# 生成、签名并安装（签名安装失败时自动回退到开发者模式松散注册）
powershell -ExecutionPolicy Bypass -File Installer\build-msix.ps1 -Install

# 不尝试签名安装，直接用开发者模式松散注册（需要先开启 Windows 开发者模式）
powershell -ExecutionPolicy Bypass -File Installer\build-msix.ps1 -Register
```

产物位于 `artifacts\msix`。正式分发时请用受信任的代码签名证书替换开发证书。

> 自签名 MSIX 在部分 Windows 版本上要求证书进入本机受信任根/受信任人存储。非管理员会话下 `-Install` 会回退到 `%LOCALAPPDATA%\Wihomo\DevLayout` 的松散注册，请不要删除该目录。若要安装签名 MSIX，请用管理员 PowerShell 执行一次：
>
> ```powershell
> certutil -addstore -f Root artifacts\certs\Wihomo.DevCert.cer
> ```


> 从旧版（Inno Setup 安装的 WPF 版本）升级时，请先在“设置 → 应用”中卸载旧版，再安装 MSIX，避免两个版本同时开机自启。MSIX 打包版的 Roaming 数据会被系统重定向到 `%LOCALAPPDATA%\Packages\<PackageFamilyName>\LocalCache\Roaming\Wihomo\settings.json`；未打包的调试版才直接使用 `%AppData%\Wihomo\settings.json`。卸载或重新注册包之前请先备份该目录。

卸载：

```powershell
Get-AppxPackage chemPolonium.Wihomo | Remove-AppxPackage
```

> 开发者模式松散注册的布局位于 `%LOCALAPPDATA%\Wihomo\DevLayout`；`Remove-AppxPackage` 不会删除该目录，确认不再使用后可手动删除。重新注册脚本会在卸载前备份包数据，并在注册后恢复。

## 项目结构

```
├── App.xaml / .cs           # WinUI 应用生命周期、单实例与激活管道
├── MainWindow.xaml / .cs    # 导航外壳、托盘宿主、关闭即隐藏
├── Views/                   # 10 个 NavigationView 视图
├── ViewModels/              # MVVM 视图模型（CommunityToolkit.Mvvm）
├── Models/                  # 数据模型（设置、订阅、代理组等）
├── Services/                # 服务层（内核管理、API 客户端、配置生成、订阅解析等）
│   └── Realtime/            # WebSocket 实时遥测：流量/连接/日志推送，断线自动重连
├── Branding/                # MSIX 图标资源
├── Package.appxmanifest     # MSIX 清单（含开机自启扩展）
├── Installer/               # MSIX 打包脚本
├── Wihomo.Tests/            # xUnit 测试工程
└── assets/                  # 内置 mihomo 内核与 Geo 数据文件
```

## 许可证

[GNU GPLv3](LICENSE)
