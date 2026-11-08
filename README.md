# 酿酒便利化 / Brewing Convenience

一个《Probably Stolen》的酿酒便利化模组：把「一颗颗挑葡萄、灌水、放酵母、收成品」压缩成两次点击。
**不改动任何原版数值、时间与配方**，只降低后期酿酒的操作复杂度。

> Steam 创意工坊：<https://steamcommunity.com/sharedfiles/filedetails/?id=3836042396>
> 游戏 AppID：`4348910` ｜ 模组 ID：`brewing_convenience` ｜ 物品 ID：`brewing_convenience_machine`

---

## 目录

- [功能](#功能)
- [安装](#安装)
- [使用](#使用)
- [配方](#配方)
- [从源码构建](#从源码构建)
- [项目结构](#项目结构)
- [实现要点（改代码前必读）](#实现要点改代码前必读)
- [已知问题](#已知问题)
- [版本历史](#版本历史)
- [发布到创意工坊](#发布到创意工坊)

---

## 功能

- **自动分拣**：按葡萄分值自动分成「正常果 / 小果 / 其他果」三堆；未熟、干枯、发霉一律进其他果，永不参与酿酒
- **自动配比**：按水质与现有葡萄精算，挑出分值合计刚好达标的两颗
- **自动灌水**：从储水容器取 750 ml，纯度按比例保持
- **批量开酿**：一次最多同时酿 8 瓶
- **生物反应器回收**：可复用酵母用完后自动回到酵母区，可以连续开酿
- **面板精算**：「当前可酿最优」会告诉你现在这批材料到底能到哪一档
- **原版风格贴图** + 面板内便签说明书（点击查看）

---

## 安装

**方式一（推荐）**：在 Steam 创意工坊订阅，游戏会自动下载并出现在 Mods 菜单的工坊来源里。

**方式二（手动）**：把以下文件放进 `<游戏目录>/Mods/BrewingConvenience/`

```
manifest.xml
BrewingConvenience.dll
preview.png
Textures/Items/brewer.png
Localization/zh.csv
Localization/en.csv
```

启动游戏 → Mods 菜单 → 激活 → **重启游戏**后生效。

> ⚠️ 同一个模组 ID 只能存在一份。如果你手动装了本地版，又订阅了工坊版，请删掉其中一个。

---

## 使用

1. 完成「退休酿酒师」人脉升级，之后在杰克逊博士处花 **888** 购买这台机器
2. 葡萄丢进「葡萄投放」区（也可以不开窗直接把葡萄扔到机器上），点「分拣」
3. 往「空酒瓶」放空啤酒瓶，往「酵母 / 储水」放酵母 + 一个装水的容器
4. 点「开酿」，机器自动配比、灌水、起酵
5. 两个夜晚后成酒，成品留在「发酵 / 成品」区，取走即可

面板布局（两排）：

```
葡萄投放 │ 正常果 │ 小果 │ 其他果
空酒瓶   │ 发酵/成品 │ 酵母/储水 │ 控制 + 便签
```

---

## 配方

```
wineScore = 两颗葡萄分值之和
          + 1（有 2 颗以上葡萄）
          + 1（有酵母）
          + 水质分
```

品质档位（与原版 `WineHelper.GetWineQualityTier` 一致）：

| wineScore | 品质 |
|---|---|
| ≥ 6 | 琼浆玉液 Master Vintage |
| = 5 | 臻品窖藏 Reserve Vintage |
| 3–4 | 量产酒液 Production Wine |
| 2 | 手工自酿 Home Brew |
| 1 | 粗桶劣酿 Bucket Brew |
| ≤ 0 | 难以下咽 Undrinkable |

水质档位与加分（`WATER_QUALITY_SCORE`）：

| 档位 | 纯度 | 加分 |
|---|---|---|
| 纯水 | ≥ 9990 | +1 |
| 优质水 | 9900–9989 | +1 |
| 基础水 | 9800–9899 | 0 |
| 幽灵水 | 9600–9799 | −1 |
| 铁锈水 | 9000–9599 | −2 |
| 沟渠水 | < 9000 | −3 |

模组默认瞄准 **琼浆玉液**，可在模组设置里切换为 **臻品窖藏**（要求恰好 5 分，机器会做精确匹配）。

葡萄分值（存储值 → 酿酒计算值）：

| 存储值 | 外观 | 参与计算 |
|---|---|---|
| 2 | 小果 | 2 |
| 1 | 正常 | 1 |
| 0 | 正常（显示同 1） | 0 |
| −1 | 未熟 / 干枯 / 过熟 | 0（原版对负数 +1） |
| −2 | 发霉 | −1 |

---

## 从源码构建

需要 .NET SDK（`netstandard2.1`，C# 9）。

```powershell
cd "D:\SteamLibrary\steamapps\common\Probably Stolen\Modding\BrewingConvenience"
dotnet build -c Release
```

构建后会自动把 DLL 与资源安装到 `<游戏目录>/Mods/BrewingConvenience/`（csproj 里的 `InstallMod` target）。

⚠️ 如果你已经订阅了工坊版，**构建会制造一份同 ID 的本地副本**，记得测试完删掉。

---

## 项目结构

```
Modding/BrewingConvenience/
├── manifest.xml              模组清单（ID / 版本 / 程序集列表）
├── BrewingConvenience.csproj  csproj，含构建后自动安装步骤
├── ModEntry.cs                入口：注册物品、博士上架、读档刷新、调试按钮
├── BrewerMachine.cs           机器工厂：8 个区域 + 9 块面板 + 交互回调
├── BrewerUnits.cs             运行时逻辑：分拣 / 开酿 / 分流 / 状态刷新
├── WineTags.cs                原版酿酒系统的 tag 名与数值规则（唯一事实来源）
├── Config.cs                  模组配置（目标品质切换）
├── Loc.cs                     本地化取值 + 硬编码兜底
├── Textures/Items/brewer.png  48×48 贴图（= 3×3 占地）
└── Localization/{zh,en}.csv   本地化表（key,value，无表头）
```

---

## 实现要点（改代码前必读）

这一节是这个项目里所有「反直觉」的地方，都是实测踩出来的。

### 1. 八个区域的创建顺序不可更改

游戏存档只记录「子物品属于第几个背包节点」，不记录它属于哪个 grid。所以 `BrewerMachine.Create()` 每次都必须以**完全相同的顺序**创建这 8 个 `GameGridInventory`。发布后一旦增删或重排区域，老存档里的葡萄和酒瓶就会落到错误位置。

### 2. 贴图 48×48 是硬上限

贴图的像素密度没有独立设置，引擎用 `贴图宽 ÷ 16 = 占地格数` 反推。3×3 就只能是 48×48。给 96×96 会被理解成 6×6 占地。

因此**不要试图用高清插画缩小**：上千像素的细节压进 48 个像素点必然糊。正确做法是照着 48×48 重画（大色块、深色描边、有限配色）。

调色板取自原版机器贴图采样：奶油 `#C4C4B0` / `#D8D8C8`、灰绿 `#88948A`、暖灰 `#79746E`、紫黑 `#292234`、青 `#8ABFBB`、绿 `#3D823F`、描边 `#2E2C36`（非纯黑）。

### 3. 机器窗口必须用 `MachineryPanelRow`

换成 `MachineryStack` 做窗口根节点会导致「不开窗直接把物品扔到机器上」只认第一个区域（葡萄能进，酵母/酒瓶/储水都进不去）。

代价是 `MachineryPanelRow.Validate()` 会对每个面板调 `ResizePixels(w, h)`，**把同表所有面板强制拉成同一尺寸**。所以「第二排比第一排矮」在表格容器里做不到。

> 当前版本（v0.2.x）为了两排不等高，改用了 `MachineryStack` + 自己接管投放（见第 4 条）。

### 4. 投放区是总入口

因为原版「扔到机器上」的分发只会命中第一个区域，所以把**葡萄投放区**做成总入口：`mayInventoryAddItemFunc` 接收葡萄/酵母/空瓶/装水容器，落地后由 `BrewerUnits.RouteFromIntake()` 分流到对应区域。

容量要**提前预判**（`GraphUtils.CanAccept(目标区, item)`），否则目标区满了物品会滞留在投放区占位置。

`canItemTargetThisFunc` / `onItemTargetThisFunc` 这对回调**实测不会触发**，不要依赖。

### 5. 葡萄分类用存储值，不是酿造计算值

`BERRY_CONDITION_INT` 存储值 `{-2,-1,1,2}`；而 `StartFermentation` 计算时对负数做 `+1`。如果分拣用计算值，`-1`（未熟/干枯/过熟）会变成 `0` 被误判为正常果。**分拣必须用 `WineTags.BerryRawCondition`，只有算 wineScore 时才用 `BerryValue`。**

### 6. 发酵由酒瓶自己驱动，机器不要重复驱动

`PlayerStore.FindAllItem` 用的是递归的 `GraphUtils.FindAllChildrenType`，所以机器格子里的酒瓶**每晚会被自己的 `onCycleEndSlotItemFunc` 正常推进**。机器若再手动减一次，会变成一晚出酒。

### 7. 可复用酵母会被扔到柜台上

`WineHelper.StartFermentation` 对带 `WINE_YEAST_REUSABLE_TAG` 的酵母执行 `parentInventory.Expel` + `GraphUtils.EmporiumTryAdd`，不是消耗。所以每次起酵后必须立刻 `RecoverYeast()` 把它捞回酵母区，否则只能酿一瓶。

### 8. 博士上架要挂 Early，不是 Late

`TryAddDirectSellingItemToTable` 内部走 `TryAcceptAllOrDestroy`，要求**整块**空位（3×3 的机器需要连续 9 格）。在 `OnPlaceInventorInventoryItemLate` 时博士前台已被其它货填成碎片，必然失败并被销毁。改用 `OnPlaceInventorInventoryItemEarly`（`ownedIds` 建好后、摆货之前）先占位。

判定「玩家是否已有这台机器」用 `EmporiumEntry.Instance.GetAllOwnedItems(false, true).Any(i => i.IsID(...))`；卖掉的机器会进 `soldElement`，不在遍历范围里，所以「卖掉能重新上架」自然成立。

### 9. 工厂函数不能产出带子物品的机器

在 `Create()` 里往槽位塞物品会触发：

```
GameItem 'xxx' created with DirectoryMaster contains child items,
this should not happen ... Removing all child items as a fix...
```

游戏会把子物品全部清掉。便签说明书要放到 `window.openCallback` / `machine.onLoaded` 里补。

### 10. UI 文本只能事件驱动刷新

把状态文本挂到 `onOneSecondItemFunc` 每秒改一次，文本变长变短会让整行面板重新量宽重排，实测能把主线程压住 60 秒（`[StallWatchdog]` 有记录）。现在只在「开窗 / 区域物品增删 / 每日结算」时刷，并做文本变更检测 + 批量操作时挂起。

### 11. 本地化

`Localization/<lang>.csv`，无表头，格式 `key,value`。CSV 存不了多行文本，说明书正文写在代码常量里。取不到时用 `Loc.Get(key, fallback)` 的兜底串，不会出现裸 key。

---

## 已知问题

- **F9 调试按钮「生成酿酒便利机」不生效**：`ModHelper.SpawnItem` 后 `MoveItemToInventory` 没落到预期位置。用游戏自带的生成器（Mods 菜单 → 物品生成）可以正常拿到机器。不影响正常游玩。
- **`<GameVersions />` 目前为空**：能正常加载，但公开分发前建议填上支持的游戏版本号。
- **说明便签字号**依赖 TMP 富文本 `<size=70%>`；若某次更新后窗口不走富文本，会显示成字面标签。

---

## 版本历史

| 版本 | 内容 |
|---|---|
| 0.1.0 | 首版：8 区域面板、自动分拣、自动配比灌水、批量发酵、博士上架 |
| 0.1.1 | 面板改多列换行；修复生物反应器被扔到柜台导致只能酿一瓶 |
| 0.1.2 | 葡萄只能进投放区；修复窗口判定框超宽导致点不动其它容器；去掉每秒刷新（修复 60 秒卡死） |
| 0.1.3 | 尝试自由行布局（后确认会破坏投放，已回退） |
| 0.1.4 | 空瓶/发酵区改横版 8×4；增加使用说明面板 |
| 0.1.5–0.1.6 | 压到两排八面板；去面板通风格栅条纹；说明书改成原版 postit 小纸条 |
| 0.1.7 | 修复工厂产出带子物品（游戏会清空内容物）；便签改到开窗/读档时补 |
| 0.1.8–0.1.9 | 「发酵中」移到面板标题；确认表格容器强制等高，改回自由行 + 自接管投放 |
| 0.2.0–0.2.1 | 赛博朋克贴图；便签挪到状态行，空瓶/发酵区放宽到 8 列 |
| 0.2.2 | 投放区容量预判，避免物品滞留占位 |
| 0.2.3–0.2.5 | 贴图按原版配色重绘（48×48 手绘）；去白边与格栅 |
| 0.2.6 | 博士上架改挂 Early，修复前台碎片导致上架失败 |
| 0.2.7 | 分拣改用存储值（修复未熟/干枯被误判为正常果）；酒瓶取料加可酿造过滤 |

---

## 发布到创意工坊

模组已发布：**PublishedFileID `3836042396`**

```powershell
# 1. 改代码 → 改 manifest.xml 的 ModVersion → build
dotnet build -c Release

# 2. 把 Mods/BrewingConvenience 的产物同步到 dist/workshop/
# 3. 改 dist/workshop_build.vdf 里的 changenote
# 4. 上传（账号需拥有该游戏）
E:\steamcmd\steamcmd.exe +login +workshop_build_item "D:\SteamLibrary\steamapps\common\Probably Stolen\dist\workshop_build.vdf" +quit
```

注意事项：

- `workshop_build.vdf` 里的 `publishedfileid` 必须填已有的 `3836042396`，**填 0 会新建一件物品**
- VDF 用 Valve KeyValues 格式，**不支持 `//` 注释**，编码必须是 UTF-8 无 BOM
- 工坊项根目录直接放 `manifest.xml`，不要再套一层文件夹
- 目录里只能有一个 DLL，且必须在 `manifest.xml` 的 `<Assemblies>` 里列出
- 走代理时 git/steamcmd 都可能报 `CONNECT tunnel failed, response 502`，换一个本地代理端口即可

---

## 许可

暂未指定许可证。如需转载、整合或二次分发，请先通过创意工坊页面留言联系作者。

作者：**XJia**
