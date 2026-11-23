# Gakumas Localify 官方插件

源码仓库：[官方插件](https://github.com/chihya72/gkms-localify-dmm-RE-official-plugins) · [RE 框架与 SDK](https://github.com/chihya72/gkms-localify-dmm-RE)

这是独立于 RE 框架的官方插件源码与发布仓库，包含 14 个插件、共享设置界面和文本/贴图资源更新实现。框架的 SDK、宿主、原生入口和管理器位于另一个仓库 `gkms-localify-dmm-RE`。

插件只引用 NuGet 包 `Gkms.Sdk 2.0.1`，不引用框架源码或项目，也不需要框架管理器来生成仓库清单。两个仓库可放在任意位置；构建时显式传入 SDK 包所在目录。

## 构建与发布

需要 PowerShell 7 和 .NET SDK 8 或以上。先在 RE 框架仓库运行 `Build.ps1`，取得 `out\sdk\Gkms.Sdk.2.0.1.nupkg`，或使用已准备好的同版本 SDK 包目录。然后在本仓库执行：

```powershell
$parameters = @{
    Configuration = 'Release'
    SdkFeed = 'D:\chinosk6\gkms-localify-dmm-RE\out\sdk'
}
& (Join-Path $PWD 'Plugins\Build-Repository.ps1') @parameters
if (-not $? -or $LASTEXITCODE -ne 0) { throw '官方插件构建失败' }
```

`SdkFeed` 可以指向任何存有 SDK 包的目录，不要求框架源码存在。SDK 包缓存位于本仓库的 `out\.packages`。更新 SDK 时应递增 SDK 包版本，并同步 `Plugins\Plugin.targets` 中的依赖版本。

输出均位于本仓库 `out\Release`：

| 输出 | 用途 |
| --- | --- |
| `plugin-packages\pluginmaster.json` | Gkms 数组式订阅索引 |
| `plugin-packages\index.json` | 兼容已有管理器的对象式索引 |
| `plugin-packages\Gkms.*.zip` | 14 个独立插件安装包 |
| `gakumas-local\re\official-plugins.zip` | 两种索引及所有安装包组成的离线官方仓库 |
| `gakumas-local\re\plugins` | 可合并部署的插件目录 |

`Plugins\Export-Repository.ps1 -StageRoot <输出目录>` 可单独从已构建的安装包导出索引。它根据各插件清单生成元数据，并核对包内 ID、版本、API、程序集，不依赖 RE 的 Host 或 Manager。

将离线仓库 ZIP 放入框架的 `gakumas-local\re` 目录，即可在插件库按需安装；也可将两个发行目录中的 `gakumas-local` 合并部署，所有插件默认关闭。文本、贴图数据由各自插件另行下载，框架内置字体不随插件发布。

[下载最新插件合集与独立安装包](https://github.com/chihya72/gkms-localify-dmm-RE-official-plugins/releases/latest)。框架已内置官方订阅源：

```text
https://github.com/chihya72/gkms-localify-dmm-RE-official-plugins/releases/latest/download/pluginmaster.json
```

发布构建向 `Build-Repository.ps1` 传入 `-ReleaseTag v1.0.0`，在 `out\Release\online` 生成使用该版本下载链接的两个在线索引；离线 ZIP 仍使用包内相对路径。合集标签与各插件清单版本分别管理，插件内容发生变化时递增其清单版本。

Actions 在提交和 PR 时通过 SDK NuGet 包构建插件，并运行框架联合测试与在线/离线索引检查。推送 `vX.Y.Z` 标签后，先上传完整草稿 Release，再公开索引和安装包；预发布标签不会替换稳定版订阅源。CI 中的框架检出仅用于打包 SDK 和运行测试，插件项目仍只引用 NuGet 包。

## 仓库结构

- `Plugins`：14 个插件及公共编译/打包设置；每个插件拥有独立 DLL、清单与版本。
- `Tools\Resources`：汉化资源下载、解包、回滚实现，由文本与贴图插件编译使用。
- `Gkms.OfficialPlugins.sln`：只构建官方插件。
- `Directory.Build.props`、`NuGet.Config`：独立构建配置与依赖来源。

源码迁自 `gkms-localify-dmm-RE` 当前工作区，包含拆分前尚未提交的修复，原框架 Git 历史保留。沿用原项目的 GPL-3.0 许可证，见 [LICENSE](LICENSE)。

## 官方 14 个插件

插件按用途分为五类，全部默认停用，独立订阅与更新。框架内置字体、设置面板、日志不作为可订阅插件，文本及贴图数据也不计入插件本体版本。

| 分类 | 插件 ID | 配置与资源入口 | 生效方式 |
| --- | --- | --- | --- |
| 汉化资源 | `localization` | 翻译诊断、独立文本资源更新 | 启停及启动词典需重启；剧情按请求读取 |
| 汉化资源 | `textures` | 独立贴图资源更新 | 支持启停与资源重载 |
| 开发工具 | `capture` | 文本/贴图采集选项，私有 `output` | 支持热启停，卸载保留采集结果 |
| 画面与窗口 | `frame-rate` | 目标 FPS | 热应用，停用恢复游戏帧率 |
| 画面与窗口 | `graphics` | 渲染比例、质量与细节参数 | 热应用，停用恢复 |
| 画面与窗口 | `window` | 缩放、方向、F11 无边框全屏 | 热应用，停用恢复窗口控制 |
| 画面与窗口 | `physics` | 骨骼物理、缩放、轴向限位与预设 | 场景角色热应用，停用恢复 |
| 摄影与镜头 | `camera` | 镜头模式、移动速度、角色偏移 | 热启停，退出恢复镜头及头部显示 |
| 摄影与镜头 | `gyro` | 鼠标/小键盘视角控制 | 在全景及摄影场景生效 |
| 摄影与镜头 | `auto-photo` | 三个自动摄影张数档位 | 热启停 |
| 游戏辅助 | `schedule-sp` | 无参数，插件开关 | 热启停 |
| 游戏辅助 | `card-cut-in` | 无参数，插件开关 | 在后续卡牌演出选择中生效 |
| 游戏辅助 | `ios-login` | 平台切换与充值拦截共同启停 | DMM 认证后切换，异步刷新余额 |
| 游戏辅助 | `live-unlock` | Live、服装/头饰独立子开关 | 热启停，名片维持真实持有判断 |

库中可按分类、来源和安装状态筛选，搜索覆盖 ID、各语言名称、作者、详细说明、简介和标签。详情显示配置/资源入口及重启需求。项目地址由插件作者提供，缺失时不生成虚构链接。需要重启的已启用插件仍遵守宿主的停用约束，不能通过更新绕过；可在关闭游戏后使用管理器命令行更新。



## 联合验证

RE 仓库保留已有框架与插件联合回归套件，使用本仓库的编译产物，不引用本仓库源码。在 RE 仓库执行：

```powershell
$parameters = @{
    OfficialPluginOutput = 'D:\chinosk6\gkms-localify-dmm-RE-official-plugins\out\Release'
    Configuration = 'Release'
    Overlay = $true
}
& (Join-Path $PWD 'Tests\Test-OfficialPlugins.ps1') @parameters
if (-not $? -or $LASTEXITCODE -ne 0) { throw '联合验证失败' }
```

测试覆盖全部插件的生命周期、配置、资源更新、订阅安装、失败回滚与 DX11 界面交互，不启动游戏。联合测试目录位于 RE 的 `out\Integration`，不属于任一仓库的独立发行包。
