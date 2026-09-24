# Ruina Coop

《废墟图书馆》合作联机 Mod，按[设计方案](docs/联机Mod设计方案.md)分阶段开发。阶段 0 已验证游戏加载器与 Harmony 接入。阶段 1 的双账号房间核心流程已验证。阶段 2 的只读进度镜像原型正在测试；角色认领、卡组锁和战斗联机尚未实现。

## 本地构建

要求 Windows、.NET SDK、.NET Framework 4.6 引用程序集，以及 Steam 版《废墟图书馆》。执行：

```powershell
pwsh -File scripts/pack.ps1 -GameDir 'D:\game\steamapps\common\Library Of Ruina'
```

生成物位于 `dist/RuinaCoop`。游戏程序集只作为本地编译引用，不会复制到包里或提交到 Git。Harmony 使用 NuGet 的 `HarmonyX` 2.9.0。打包时只有 `RuinaCoop.dll` 放在游戏扫描的 `Assemblies` 目录；HarmonyX 及 MonoMod DLL 放在 `Dependencies`，由入口按需加载，以免游戏将依赖重复加载并弹出错误。

关闭游戏后执行 `pwsh -File scripts/install-local.ps1` 安装测试包；脚本会先把旧包备份到忽略目录 `artifacts/install-backups`。也可以手动将整个 `dist/RuinaCoop` 文件夹放入游戏的 `LibraryOfRuina_Data/Mods`，再通过 Steam 的 **LOR With Mods** 启动游戏，并在 Mod 列表中勾选 **Ruina Coop (Development)**。该启动选项使用 `-mod` 参数；见 [SteamDB 的游戏配置](https://steamdb.info/app/1256670/config/)。初次加载时应在 `%USERPROFILE%\\AppData\\LocalLow\\Project Moon\\LibraryOfRuina\\Player.log` 中看到 `[RuinaCoop] Stage 0 bootstrap loaded` 和 `[RuinaCoop] Harmony game-loop probe reached`。未验证的游戏程序集版本会拒绝打补丁并记录 SHA-256。

当前不会写入游戏存档或修改原版程序集。初版范围已定为原版游戏加本 Mod，客人不会获得永久关卡解锁。
## 阶段 1 房间测试

在主菜单按 **F9** 打开房间面板。Steam 状态应为 `ready`。房主点击 `Create public room (max 5)` 或 `Create friends-only room (max 5)`，通过面板中的 Room ID 或 `Invite Steam friends` 邀请客人。客人可以输入房间 ID、搜索公开房间，或接受 Steam 邀请。双方都能查看成员列表并离开房间。房主离开时，客人自动退出房间。

目前只提供房间测试，不应据此开始联机接待。阶段 1 仍需两个 Steam 账号及最多五人验证，见 [阶段 1 验证记录](docs/阶段1验证.md)。
## 阶段 2 进度原型

房主需先通过“继续游戏”载入图书馆存档。开房后，F9 右侧显示房主章节、可用原版关卡、楼层与馆员。客人加入后经 Steam Relay 接收相同的只读快照；房主可以在该面板选择一个“下一关计划”，选择尚未触发原版接待。旧的阶段 1 测试包协议不兼容，双端必须使用相同的阶段 2 测试包。详见 [阶段 2 验证记录](docs/阶段2验证.md)。