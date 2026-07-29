# Wihomo

基于 [mihomo](https://github.com/MetaCubeX/mihomo) 内核的 Windows 代理客户端，使用 WPF (.NET 9) 构建。

## 功能

- **内核管理** — 启动/停止/重启 mihomo 内核，实时查看连接数、流量速率与累计用量
- **订阅管理** — 添加、编辑、删除订阅，支持自动解析订阅流量与到期信息
- **代理组切换** — 查看代理组及组成员，一键切换节点
- **外部资源** — 配置 GeoIP / GeoSite / MMDB 等外部数据文件的下载地址与自动更新
- **YAML 覆写** — 通过 YAML 片段自定义 mihomo 配置
- **规则与连接** — 查看当前规则列表和活跃连接
- **系统代理 / TUN 模式** — 支持系统代理和 TUN 模式两种流量接管方式
- **开机自启** — 可选随 Windows 启动并自动运行内核
- **系统托盘** — 最小化到托盘，双击恢复窗口

## 环境要求

- Windows 10/11 x64
- .NET 9 Runtime

## 构建

```bash
dotnet build
```

## 运行

```bash
dotnet run
```

## 项目结构

```
├── MainWindow.xaml / .cs    # 主窗口 UI 与逻辑
├── Models/                  # 数据模型（设置、订阅、连接等）
├── Services/                # 服务层（内核管理、API 客户端、配置生成等）
├── assets/                  # 内置 mihomo 内核与 Geo 数据文件
└── Installer/               # Windows 安装包配置
```

## 许可证

[GNU GPLv3](LICENSE)
