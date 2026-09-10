# RefTile.PluginMigrator —— 插件迁移工具

## 这是什么

RefTile.PluginMigrator 是一个 IL 重写工具（基于 Mono.Cecil / MonoMod），用于把**依赖官方 TShock/TSAPI 的旧插件**转换成**兼容本项目 Ref Tile 模型**的插件。

原因：官方 TSAPI 的方块系统是**类模型**（`Terraria.ITile` 接口 / `Terraria.Tile` 引用类，每格一个堆对象）；本项目（Ref-Tile 系列）把它改成了**值类型模型**（`Terraria.TileData` 16 字节结构体，`Main.tile` 为 `TileCollection` 连续存储）。直接用旧 API 编译的插件在运行时会对不上类型引用与内存布局而崩溃，迁移工具在 **IL 层**把这些引用就地改写为新模型。

## 它能改写什么

| 旧模型（官方） | 新模型（Ref Tile） |
|---|---|
| `Terraria.ITile` / `Terraria.Tile` 类型引用 | `Terraria.TileData`（值类型） |
| `Main.tile`（`Tile[,]` / `ITile[,]` 字段） | `Terraria.TileCollection`，访问改为 `get_Item` 返回 `ref TileData` |
| 插件自有的 `Tile[,]` / `ITile[,]` 字段、局部变量、泛型参数 | `TileData[,]`（结构体数组） |
| `new Tile()` / `new Tile(...)` | `TileData.New(...)`（静态工厂） |
| 属性访问器（`tile.type`、`tile.wall` …） | 直接字段访问（`ldfld` / `stfld`） |
| `tile == null` / `if (tile)` 判空 | `TileData.NULL` 哨兵值比较 / `IsNull` / `IsNotNull` |
| 需要跨作用域持有的方块引用 | `RefTileData`（堆安全包装） |

同时修复值类型特有的 IL 问题：`this` 必须按地址加载（`ldloca` / `ldarga`）、`get_Item` 返回 `ref` 后消费方补 `ldobj`、`unbox.any` 前先 `box` 等，避免运行期访问违例（0xC0000005）。

## 获取方式

### 单独构建

```bash
dotnet build TShockPluginMigrator -c Release
```

产物在 `TShockPluginMigrator/bin/Release/net9.0/`（exe + 全部依赖），复制到服务器目录（`TShock.Server.exe` 所在位置）即可使用。

## 使用方法

在服务器目录（`TShock.Server.exe` 所在目录）下运行：

### 一键迁移全部插件

```bash
RefTile.PluginMigrator
```

自动迁移 `ServerPlugins/` 下所有 `*.dll`，**就地改写**，每个文件保留 `.bak` 备份。

### 指定文件 / 输出目录

```bash
RefTile.PluginMigrator MyPlugin.dll AnotherPlugin.dll -o migrated
```

`-o` 指定输出目录（不指定则就地改写并保留 `.bak`）。

### 指定参考程序集

```bash
RefTile.PluginMigrator MyPlugin.dll -ref C:\path\to\OTAPI.dll
```

当解析 Terraria 类型失败时，用 `-ref` 指向 Ref Tile 构建的 OTAPI / TerrariaServer 程序集所在目录。

### 通用转换模式

```bash
RefTile.PluginMigrator MyPlugin.dll --universal
```

使用与游戏本体 patch 完全相同的 `TileSystemPatchLogic` 转换机制（能处理模式化重写覆盖不到的边角场景），并自动追加一遍模式化补丁。

### 自检

```bash
RefTile.PluginMigrator --selftest
```

编译一个模拟旧模型的插件并验证改写结果，适合确认工具本身是否正常。

## 迁移后的注意事项

- 插件代码语义不变，但热点代码建议改用 `ref TileData` 局部变量：

  ```csharp
  ref TileData tile = ref Main.tile[x, y];
  tile.type = TileID.Stone;
  tile.active(true);
  ```

- 判空用 `tile.IsNull` / `tile.IsNotNull`（结构体不可能为 null）
- 把方块引用存进字段 / 集合 / 闭包 / async 时用 `RefTileData` 包装
- 迁移前先备份；建议先在测试服务器上验证再上生产

## 构建与开发

```bash
dotnet build TShockPluginMigrator -c Release
```

`UniversalLogic.cs` 是从 Ref-Tile-Open-Terraria-API 仓库的 `OTAPI.Scripts/Mods/RefTile.Server.cs` 自动提取的（与游戏 patch 同源）：

- `extract.ps1` —— 重新提取 `TileSystemPatchLogic` 及辅助类（需先在 OTAPI 仓库改好再跑，输出到本项目的 `TShockPluginMigrator/UniversalLogic.cs`）
- `fix-refs.ps1` —— 把编译期 `typeof(Terraria.X)` / `nameof(...)` 转成字符串字面量（工具不能引用 OTAPI 程序集）
