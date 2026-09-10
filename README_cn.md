<p align="center">
  <img src="https://tshock.s3.us-west-001.backblazeb2.com/newlogo.png" alt="Ref-Tile-TShock"><br />
  <a href="https://github.com/UnrealMultiple/Ref-Tile-TShock/actions/workflows/ci-otapi3.yml">
    <img src="https://github.com/UnrealMultiple/Ref-Tile-TShock/actions/workflows/ci-otapi3.yml/badge.svg" alt="GitHub Actions 构建状态">
  </a>
  <a href="https://github.com/UnrealMultiple/Ref-Tile-TShock/blob/general-devel/README.md">English</a>
</p>

# Ref-Tile-TShock

**Ref-Tile-TShock 是 [TShock](https://github.com/Pryaxis/TShock) 的分支（fork）**，构建于 Ref-Tile 生态之上：以 [OTAPI.RefTile](https://www.nuget.org/packages/OTAPI.RefTile) 和 [Ref-Tile-TSAPI](https://github.com/UnrealMultiple/Ref-Tile-TSAPI) 为基础，完整保留 TShock 的功能（反作弊、服务端存档、用户组、权限、物品禁用、海量指令……），同时借助 **Ref Struct Tile 模式**大幅降低内存占用。

## 核心优势

### 🚀 Ref Struct Tile：大幅节省内存

原版 Terraria 把每一格方块都存成**堆上的引用类对象**；本项目的 Ref Struct Tile 模式把它改写成 **16 字节的值类型结构体** `Terraria.TileData`，并按连续内存存储：

| | 原版 / 传统 OTAPI | Ref-Tile（本项目） |
|---|---|---|
| 方块表示 | `Terraria.Tile`（引用类，每格一个堆对象） | `Terraria.TileData`（16 字节结构体，内联存储） |
| 世界存储 | `Tile[,]` —— 对象引用数组 | `TileCollection` —— 连续 `TileData` 存储 |
| 方块访问 | `Main.tile[x, y]` 返回堆对象引用 | 返回 `ref TileData`（指向存储区的托管指针） |

- **大世界立省数百 MB**：约 8400×2400（约 2000 万格）的世界，存储从**超过 1 GB 的零散堆对象降到约 320 MB 的连续结构体数据**。
- **GC 压力基本消失**：不再为每格分配一个被 GC 跟踪的堆对象，世界生成、区块流式传输、游戏内编辑不再产生海量垃圾。
- **缓存友好**：连续内存让方块遍历变成顺序读，光照、液体模拟、地图渲染全面受益。
- **零拷贝原地修改**：`Main.tile[x, y].type = …` 直接写入存储槽位；热点代码建议用 `ref TileData tile = ref Main.tile[x, y];`。

> 完整技术细节见 [Ref Struct Tile 文档](https://github.com/UnrealMultiple/Ref-Tile-Open-Terraria-API/blob/upcoming/docs/ref-struct-tile.zh-CN.md)。

### ⚙️ 完整保留 TShock 功能

反作弊、服务端存档、用户组与权限、物品禁用、指令系统等与官方 TShock 一致，插件 API 兼容，现有 TShock 插件可直接编译运行。

### 📦 提供 NuGet 包

- [`Ref-Tile-TShock`](https://www.nuget.org/packages/Ref-Tile-TShock) —— TShock API 包（nuget.org）
- 使用 **NuGet 可信发布**（OIDC）自动化构建发布，无需长期 API 密钥

### 🌍 多平台支持

`win-x64`、`osx-x64`、`linux-x64`、`linux-arm64`、`linux-arm`，可发布为单文件可执行程序。

## 从源码构建

需要 [.NET 9 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/9.0)。

```bash
git clone https://github.com/UnrealMultiple/Ref-Tile-TShock.git --recurse-submodules
cd Ref-Tile-TShock
dotnet build
```

启动服务器（TShockLauncher）：

```bash
dotnet run --project TShockLauncher
```

打包发行版：

```bash
cd TShockLauncher
dotnet publish -r win-x64 -f net9.0 -c Release -p:PublishSingleFile=true --self-contained false
```

产物在 `TShockLauncher/bin/Release/net9.0/<架构>/` 目录下。

## 插件开发

与官方 TShock 一致：由于 OTAPI 的原因，`Terraria` 命名空间的所有字段均为 public，插件可以直接访问本体代码（相当于 CraftBukkit 的 NMS 调用）。

本项目是 **Ref Struct Tile 模式**，插件开发时注意：

- 方块类型是值类型 `Terraria.TileData`（不再是引用类 `Terraria.Tile`）
- 热点代码用 `ref TileData tile = ref Main.tile[x, y];` 保存局部引用
- 判空用 `tile.IsNull` / `tile.IsNotNull`（结构体不可能为 null）
- 需要跨作用域保存方块引用（字段、集合、闭包、async）时，用 `RefTileData` 包装

## 插件迁移工具（RefTile.PluginMigrator）

官方 TShock/TSAPI 插件是基于**类**的方块模型（`Terraria.ITile` / `Terraria.Tile`）编译的，无法直接在本项目的 Ref Tile 模型（`Terraria.TileData` 结构体）下运行。仓库自带的 **`RefTile.PluginMigrator`** 是一个 IL 重写工具，可自动把这类插件转换成本项目兼容的插件：

- **原理**：使用 Mono.Cecil / MonoMod 对插件程序集做 IL 级重写，把 `ITile` / `Tile` 类型、字段、属性访问、方法调用、`newobj`、判空以及 `Main.tile` 访问全部改写为 `TileData` / `TileCollection`（与游戏本体 patch 使用同一套转换逻辑）。
- **构建**：`dotnet build TShockPluginMigrator -c Release` 单独编译，产物在 `TShockPluginMigrator/bin/Release/net9.0/`，可手动复制到服务器目录使用。

使用方法（在服务器目录下运行）：

```bash
# 迁移 ServerPlugins 目录下所有插件（就地改写，原文件保留 .bak 备份）
RefTile.PluginMigrator

# 指定文件 / 输出目录 / 参考程序集目录
RefTile.PluginMigrator MyPlugin.dll -o migrated -ref C:\path\to\OTAPI.dll

# 自检（编译一个模拟旧插件并验证改写结果）
RefTile.PluginMigrator --selftest
```

> 建议迁移前备份插件；迁移后可先 `--selftest` 验证工具本身正常。详细说明见 [docs/plugin-migrator.md](docs/plugin-migrator.md)。

## 许可证

[GPL-3.0-or-later](https://www.gnu.org/licenses/gpl-3.0.html)（与上游 TShock 一致）。
