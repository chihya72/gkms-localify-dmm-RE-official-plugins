# Gakumas Localify 官方插件

[RE 框架与 SDK](https://github.com/chihya72/gkms-localify-dmm-RE) · [源码](https://github.com/chihya72/gkms-localify-dmm-RE-official-plugins)

本仓库维护 14 个官方插件的源码、安装包和订阅索引。插件使用自己的版本号，通过本仓库订阅源独立发布，不创建 GitHub Release，也不在框架 Release 中记录插件更新。框架和 SDK 的下载、安装与更新见 [RE 框架仓库](https://github.com/chihya72/gkms-localify-dmm-RE)。

## 订阅安装

在框架的「订阅源」中使用以下地址，新版框架已内置：

```text
https://raw.githubusercontent.com/chihya72/gkms-localify-dmm-RE-official-plugins/main/pluginmaster.json
```

在「插件库」选择插件安装，再到「已安装」启用。文本和贴图资源在对应插件的「资源更新」中下载。

框架和插件可分别更新；插件包所需的 API 等级须受当前框架支持。当前官方插件使用 API 2。旧版框架若仍指向已停用的插件 Release 地址，请改用上述订阅地址，或升级框架后恢复默认官方源。

根目录的 `pluginmaster.json` 和 `index.json` 由 Actions 自动生成；`packages` 保存带版本号的 ZIP。索引中的下载地址指向保存安装包的 Git 提交，缓存的旧索引仍能下载对应版本。

## 构建与自动发布

需要 PowerShell 7、.NET SDK 8 或以上及 `Gkms.Sdk.2.0.4.nupkg`。SDK 从[框架 Release](https://github.com/chihya72/gkms-localify-dmm-RE/releases/latest) 下载，或在框架源码中打包。

```powershell
$parameters = @{
    SdkFeed = (Join-Path $PWD '..\gkms-localify-dmm-RE\out\sdk')
}
& (Join-Path $PWD 'Plugins\Build-Repository.ps1') @parameters
if (-not $? -or $LASTEXITCODE -ne 0) { throw '官方插件构建失败' }
```

输出位于 `out\Release`：`plugin-packages` 是安装包和本地索引，`online` 是 Raw 索引，`gakumas-local\re\official-plugins.zip` 是供本地使用的离线合集。插件只通过 SDK NuGet 包构建，不引用框架实现项目。

Actions 在 PR 和源码提交时构建并执行联合测试；只有 `main` 验证通过后，才将 ZIP 和索引提交回同一仓库。工作流使用框架源码生成 SDK 并运行兼容性测试，不创建或修改框架 Release，不要求先发布同版本的框架。插件功能变化时递增对应 `plugin.json` 的版本，不需要插件合集标签或 Release。更新同时保留旧包的 Git 历史。

## 修改插件资料

各插件的 `Plugins\<插件目录>\plugin.json` 是资料来源：`name` 为名称，`punchline` 为列表简介，`description` 为详细说明，`author` 为作者，`version` 为插件版本；还可修改 `category`、`tags`、`names`（多语言名称）和可选的 `repositoryUrl`。`id` 用于识别安装、配置及订阅归属，不应随显示名称一起改动。

更新清单后重新构建，由发布流程同步安装包与索引。不要只修改根目录的 `index.json` 或 `pluginmaster.json`，否则下一次发布会覆盖，安装包资料也会与列表不一致。已发布资料的修改同样应递增插件版本，便于用户获取更新。

## 官方 14 个插件

插件按用途分为五类，全部默认停用，独立订阅与更新。框架内置字体、设置面板、日志不作为可订阅插件，文本及贴图数据也不计入插件本体版本。

| 分类 | 插件 ID | 配置与资源入口 | 生效方式 |
| --- | --- | --- | --- |
| 汉化资源 | `localization` | 翻译诊断、独立文本资源更新 | 启停及启动词典需重启；剧情按请求读取 |
| 汉化资源 | `textures` | 独立贴图资源更新 | 支持启停与资源重载 |
| 开发工具 | `capture` | 文本/贴图采集选项，私有 `output` | 支持热启停，卸载保留采集结果 |
| 画面与窗口 | `frame-rate` | 目标 FPS | 热应用，停用恢复游戏帧率 |
| 画面与窗口 | `graphics` | 渲染比例、质量与细节参数 | 热应用，停用恢复 |
| 画面与窗口 | `window` | 自由调整大小、方向、F11 当前屏幕无边框全屏 | 热应用，停用恢复窗口控制 |
| 画面与窗口 | `physics` | 骨骼物理、缩放、轴向限位与预设 | 场景角色热应用，停用恢复 |
| 摄影与镜头 | `camera-control` | 镜头模式、移动速度、角色偏移 | 热启停，退出恢复镜头及头部显示 |
| 摄影与镜头 | `gyro` | 鼠标/小键盘视角控制 | 在全景及摄影场景生效 |
| 摄影与镜头 | `auto-photo` | 三个自动摄影张数档位 | 热启停 |
| 游戏辅助 | `schedule-sp` | 无参数，插件开关 | 热启停 |
| 游戏辅助 | `card-cut-in` | 无参数，插件开关 | 在后续卡牌演出选择中生效 |
| 游戏辅助 | `ios-login` | 平台切换与充值拦截共同启停 | DMM 认证后切换，异步刷新余额 |
| 游戏辅助 | `live-unlock` | Live、服装/头饰独立子开关 | 热启停，名片维持真实持有判断 |

窗口尺寸调节的旋转选项显示为「自动旋转」「强制竖屏」「强制横屏」，旧配置值继续兼容。从窗口插件 1.0.3 起，F11 强制在游戏窗口所在显示器无边框全屏，不受旋转方式或「自由调整大小」开关限制；再按一次恢复原位置和尺寸。

镜头控制从 1.0.3 起使用 ID `camera-control`。旧 ID `camera` 的包请先停用，再从订阅源安装新 ID。新版框架在检测到新包后兼容旧镜头配置，并优先保留新 ID 已保存的参数；新包通过其安装来源建立独立订阅归属。仅改名不会让旧框架把不同 ID 自动视为同一个更新。

当前 14 个插件的作者字段均为 `Gakumas Localify contributors`，未在清单中逐项列出个人贡献者。

库中可按分类、来源和安装状态筛选，搜索覆盖 ID、各语言名称、作者、详细说明、简介和标签。详情显示配置/资源入口及重启需求。项目地址由插件作者提供，缺失时不生成虚构链接。需要重启的已启用插件仍遵守宿主的停用约束，不能通过更新绕过；先在面板停用并保存，重新启动游戏后再从插件库更新。

## 联合验证

RE 仓库保留已有框架与插件联合回归套件，使用本仓库的编译产物，不引用本仓库源码。在 RE 仓库执行：

```powershell
$parameters = @{
    OfficialPluginOutput = (Join-Path $PWD '..\gkms-localify-dmm-RE-official-plugins\out\Release')
    Configuration = 'Release'
    Overlay = $true
}
& (Join-Path $PWD 'Tests\Test-OfficialPlugins.ps1') @parameters
if (-not $? -or $LASTEXITCODE -ne 0) { throw '联合验证失败' }
```

测试覆盖全部插件的生命周期、配置、资源更新、订阅安装、失败回滚与 DX11 界面交互，不启动游戏。联合测试目录位于 RE 的 `out\Integration`，不属于任一仓库的独立发行包。
