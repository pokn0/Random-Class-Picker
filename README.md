# Random Class Picker

FF14（最终幻想14）卫月插件：**用真随机抽一个职业，或抽一个每日随机任务，直接切过去 / 排上队。**

面向国服（XIVLauncherCN）开发，基于 Dalamud API 15 / .NET 10。

## 功能

**随机换职业**

- 从你已保存的套装里随机抽一个职业并**直接切换**
- 随机数来自 **[random.org](https://www.random.org/)** 的大气噪声（物理熵源，不是伪随机算法）
- 界面用颜色**明确标注**每次是真随机还是本地伪随机兜底；也可设为"只用真随机"，拿不到就报错
- 按职能抽签：`/rcp tank` / `healer` / `melee` / `ranged` / `caster`
- 职业池面板：逐个勾选哪些职业进入随机池；生产/采集职业**永久排除**
- 同一职业多个套装时只取**品级最高**的那套，避开装备缺件的旧套装
- **抽中次数统计**（含占比、是否在池中及原因），跨重启保留

**随机每日任务**

- 在游戏任务搜索器窗口上叠加面板：每个随机任务一个复选框 + 一个抽选按钮
- 从"已勾选且未完成"的任务里随机抽一个并**直接发起参加申请**
- 已完成的任务自动置灰不可选，第二天重置后自动恢复

**快捷入口**

- 服务器信息栏（DTR）图标：**左键点一下即抽即切**，不用开窗口、不用输指令

## 安装

1. 从 [Releases](https://github.com/pokn0/Random-Class-Picker/releases) 下载 `RandomClassPicker.zip` 并解压
2. 把 `RandomClassPicker.dll` 与 `RandomClassPicker.json` 放到
   `%APPDATA%\XIVLauncherCN\devPlugins\RandomClassPicker\`
3. 游戏内 `/xlplugins` → 开发者设置 → 启用开发者模式 → 启用 **Random Class Picker**

> 也可以把本仓库加入自定义插件仓库（见 `repo.json`）。

## 指令

| 指令 | 说明 |
| --- | --- |
| `/rcp` | 打开插件窗口 |
| `/rcp roll` | 全职业池随机 |
| `/rcp tank` `/healer` `/melee` `/ranged` `/caster` | 按职能随机 |
| `/rcp daily` | 打开任务搜索器（随机每日任务面板） |
| `/rcp roulette` | 列出全部随机任务及其状态 |
| `/rcp list` | 列出所有套装 |
| `/rcp status` | 查看当前筛选结果 |
| `/rcp debug` | 打印职业职能映射与套装快照 |

## 从源码构建

需要 **.NET SDK 10.0** 与一份已安装的卫月（提供 Dalamud 开发程序集）。

```powershell
# 国服：程序集在 %APPDATA%\XIVLauncherCN\addon\Hooks\dev\
# 国际服：把 DALAMUD_HOME 指向 %APPDATA%\XIVLauncher\addon\Hooks\dev\
dotnet build RandomClassPicker/RandomClassPicker.csproj -c Debug
```

`Directory.Build.props` 会把 `DALAMUD_HOME` 默认指向国服目录；国际服或 CI 环境下用环境变量覆盖即可。

编译产物默认输出到 `%APPDATA%\XIVLauncherCN\devPlugins\RandomClassPicker\`，游戏内 Reload 即可。

## 开发笔记

`RandomClassPicker/README.md` 里有一份相当详细的实现笔记，记录了开发过程中踩到的坑，
包括：

- 为什么 `Window` 必须注册到 `WindowSystem` 才会显示
- `DtrInteractionEvent.ModifierKeys` 的枚举类型不是 `ImGuiKey`（编译期不报错的运行时异常）
- `GearsetEntry.Name` 是 `Span<byte>` 而非字符串
- 套装编号是稀疏的，不能用 `NumGearsets` 当循环上界
- 随机任务顺序既不等于 RowId 升序、也不等于表内部顺序
- `dalamud.log` 写满 100 MiB 后就不再记录

## 授权

[MIT](LICENSE)
