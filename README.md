# Ruina Coop

《废墟图书馆》合作联机 Mod，按[设计方案](docs/联机Mod设计方案.md)分阶段开发。当前是阶段 0 的代码型 Mod 骨架，只验证游戏加载器与 Harmony 接入；还没有房间、进度或战斗联机功能。

## 本地构建

要求 Windows、.NET SDK、.NET Framework 4.6 引用程序集，以及 Steam 版《废墟图书馆》。执行：

```powershell
pwsh -File scripts/pack.ps1 -GameDir 'D:\game\steamapps\common\Library Of Ruina'
```

生成物位于 `dist/RuinaCoop`。游戏程序集只作为本地编译引用，不会复制到包里或提交到 Git。Harmony 使用 NuGet 的 `HarmonyX` 2.9.0。打包时只有 `RuinaCoop.dll` 放在游戏扫描的 `Assemblies` 目录；HarmonyX 及 MonoMod DLL 放在 `Dependencies`，由入口按需加载，以免游戏将依赖重复加载并弹出错误。

将整个 `dist/RuinaCoop` 文件夹放入游戏的 `LibraryOfRuina_Data/Mods`，再通过 Steam 的 **LOR With Mods** 启动游戏，并在 Mod 列表中勾选 **Ruina Coop (Development)**。该启动选项使用 `-mod` 参数；见 [SteamDB 的游戏配置](https://steamdb.info/app/1256670/config/)。初次加载时应在 `%USERPROFILE%\\AppData\\LocalLow\\Project Moon\\LibraryOfRuina\\Player.log` 中看到 `[RuinaCoop] Stage 0 bootstrap loaded` 和 `[RuinaCoop] Harmony game-loop probe reached`。未验证的游戏程序集版本会拒绝打补丁并记录 SHA-256。

当前不会写入游戏存档或修改原版程序集。初版范围已定为原版游戏加本 Mod，客人不会获得永久关卡解锁。