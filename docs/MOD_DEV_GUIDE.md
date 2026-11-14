# Probably Stolen — Mod 开发交接手册

> 这份文档写给「下一个接手的人（或 AI）」：读完就能开工，不用再从头摸 API、也不用重踩已经踩过的坑。
> 所有路径基于当前这台机器，换电脑时只需改「第 1 节」的路径表和 `tools/psd.py` 里的默认 DLL 路径。

---

## 0. 三十秒速览

| 项 | 值 |
|---|---|
| 引擎 | Unity 2021.3，**Mono**（不是 IL2CPP，所以能直接反编译 `Assembly-CSharp.dll`） |
| 目标框架 | `netstandard2.1` + `LangVersion 9.0`（不要改成 latest，游戏运行时只到 C# 9） |
| 构建 | `dotnet build -c Release`，产物约 28 KB |
| 加载方式 | `Mods/<文件夹>/` 放 `manifest.xml` + dll + 资源，游戏内 Mods 菜单激活后**重启** |
| 查 API | `python Modding/BrewingConvenience/tools/psd.py ...`（反编译 Assembly-CSharp.dll） |
| 日志 | `%USERPROFILE%\AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log` |
| AppID | `4348910` |
| 现有实现 | `Modding/BrewingConvenience`（酿酒便利化 v1.1.0，可直接当模板抄） |

---

## 1. 环境与路径

| 用途 | 路径 |
|---|---|
| 游戏根目录 | `D:\SteamLibrary\steamapps\common\Probably Stolen` |
| 游戏程序集 | `Probably Stolen_Data\Managed\Assembly-CSharp.dll` |
| Unity 模块 | 同目录下 `UnityEngine*.dll`、`Unity.TextMeshPro.dll`、`0Harmony.dll` |
| 游戏日志 | `C:\Users\<你>\AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log` |
| 上一个日志 | 同目录 `Player-prev.log` |
| 本地 mod 目录 | `<游戏目录>\Mods\<任意文件夹>\` |
| 工坊订阅内容 | `<Steam>\steamapps\workshop\content\4348910\<PublishedFileID>\` |
| 工具 | `Modding\BrewingConvenience\tools\psd.py` |
| 深度调研 | `Modding\BrewingConvenience\docs\research\*.md`（酿酒/容器 UI/博士/水，共 4 份） |

### 反编译工具用法

依赖两个 pip 包，新机器先装一次：

```powershell
python -m pip install dnfile dncil
```

```powershell
cd "D:\SteamLibrary\steamapps\common\Probably Stolen\Modding\BrewingConvenience\tools"

python psd.py types "Wine"            # 按正则找类型
python psd.py sig   "MachineryText$"  # 列类型成员（字段/方法/签名）
python psd.py il    "WineHelper" "StartFermentation"   # 反汇编某个方法
python psd.py consts "MachineryStyle" # 列常量值
python psd.py xr    "onCycleEnd"      # 交叉引用：谁调用了它
python psd.py ustr  "wine_score"      # 在 #US 堆里搜字符串字面量
```

DLL 路径优先读环境变量 `PS_DLL`，换机器时设这个变量就不用改代码。

> 经验：**别猜 API**。改任何一行调用前，先用 `sig` 看签名、用 `il` 看语义。这个游戏的 API 命名很直观，但语义经常和名字不一致（见第 4 节）。

---

## 2. 新建一个 Mod：五步

### 2.1 目录结构

```
Modding\<ModName>\
├── <ModName>.csproj
├── manifest.xml
├── ModEntry.cs                    # IMod 实现
├── Textures\
│   └── Items\<sprite>.png         # 物品贴图
└── Localization\
    ├── zh.csv
    └── en.csv
```

发布/调试时整体拷到 `Mods\<mod_id>\`。

### 2.2 csproj 模板

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <AssemblyName>YourMod</AssemblyName>
    <RootNamespace>YourMod</RootNamespace>
    <Nullable>disable</Nullable>
    <NoWarn>CS0169;CS0414;CS0649</NoWarn>
    <GameDir>$(MSBuildProjectDirectory)\..\..</GameDir>
    <ManagedDir>$(GameDir)\Probably Stolen_Data\Managed</ManagedDir>
    <ModInstallDir>$(GameDir)\Mods\your_mod_id</ModInstallDir>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Assembly-CSharp">
      <HintPath>$(ManagedDir)\Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>$(ManagedDir)\UnityEngine.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>$(ManagedDir)\UnityEngine.CoreModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityEngine.TextRenderingModule">
      <HintPath>$(ManagedDir)\UnityEngine.TextRenderingModule.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Unity.TextMeshPro">
      <HintPath>$(ManagedDir)\Unity.TextMeshPro.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <ModFile Include="manifest.xml;preview.png;Textures\**;Sounds\**;Localization\**;LootTables\**;Books\**" />
    <None Remove="Textures\**;Sounds\**;Localization\**;LootTables\**;Books\**" />
  </ItemGroup>

  <Target Name="InstallMod" AfterTargets="Build" Condition="'$(InstallLocal)' == 'true'">
    <MakeDir Directories="$(ModInstallDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(ModInstallDir)" />
    <Copy SourceFiles="@(ModFile)" DestinationFolder="$(ModInstallDir)\%(RelativeDir)" Condition="Exists('%(ModFile.FullPath)')" />
  </Target>
</Project>
```

四个必须注意的点：

1. **`%(RelativeDir)` 不能换成 `%(RecursiveDir)`**。官方 `create-mod.bat` 用的是后者，它会吞掉 `Textures\`、`Localization\` 这层前缀，把文件摊平到根目录；而游戏扫资源是「路径精确、不递归」，摊平就等于没装。
2. **`<Private>false</Private>` 必须加**。否则编译器会把 `Assembly-CSharp.dll` 复制进输出目录，mod 文件夹里出现第二个 DLL，游戏会拒绝激活。
3. **默认不自动安装**（`-p:InstallLocal=true` 才装）。工坊版和本地版 mod ID 相同时，两份并存会让游戏随机取一份，测试结果不可信。
4. **输出目录里只能有一个 DLL**，且必须列在 `manifest.xml` 的 `<Assemblies>` 里。

### 2.3 manifest.xml 模板

```xml
<?xml version="1.0" encoding="utf-8"?>
<Manifest>
  <ID>your_mod_id</ID>
  <Name>显示名</Name>
  <Author>作者</Author>
  <ModVersion>1.0.0</ModVersion>
  <Description>一句话说明。</Description>
  <GameVersions />
  <Prerequisites />
  <Assemblies>
    <Assembly>YourMod.dll</Assembly>
  </Assemblies>
  <Capabilities />
</Manifest>
```

- `<ID>` **一旦发布就永远不要改**：存档、配置、工坊订阅全绑它。
- `<GameVersions />` 留空可以正常加载。
- `<Capabilities />` 留空表示不声明反射/文件/网络能力。

### 2.4 入口

```csharp
public class ModEntry : IMod
{
    public void Init(ModManifest manifest) { }   // 主菜单阶段，还没有存档
    public void OnEnable()
    {
        ModHook.OnModItemDirectoryInit += OnItemDirectoryInit;  // 注册物品
        ModHook.OnGameLoadedLate += OnGameLoaded;               // 存档就绪
    }
    public void OnDisable() { /* 记得 -= 全部取消订阅 */ }
}
```

`Init` / `OnEnable` 都在主菜单执行，**此时没有存档**，任何依赖存档数据的逻辑都要挪到 `OnGameLoaded*`。

### 2.5 贴图规则（最容易返工的一条）

```
贴图宽度 ÷ 16 = 占地格数
```

| 占地 | 贴图必须 |
|---|---|
| 1×1 | 16×16 |
| 2×2 | 32×32 |
| **3×3** | **48×48** |
| 4×4 | 64×64 |

给 96×96 不会被当成"更清晰"，而是被理解成 6×6 占地，机器突然变成 4 倍大。

---

## 3. 常用 API 速查

以下全部来自 `Assembly-CSharp.dll` 实测签名。

### 3.1 物品 / 网格

```csharp
GameItem item = DirectoryMaster.Item("item_id", true);   // 从物品表取
item.name / item.shortDescription / item.flavorText
item.SetGameItemType("DOCUMENT");
item.unitValue = 0L;
item.SetSpriteAndShapeFromMod(modId, "sprite_key");       // 用 mod 贴图
item.SetShape(new GridShapeBuilder(3, 3).SetDataFill(1).Build());
item.IsTag("XXX") / GetTagReadonly("XXX") / ModifyTag("XXX", st => ..., false)
item.EnableTag("XXX") / DisableTag("XXX")

new GameGridInventory(cols, rows)          // 网格容器
new GameSlotInventory()                    // 单槽容器
inv.childItems                             // 当前物品
inv.mayInventoryAddItemFunc = (item, inv) => bool;
inv.onSlotAddItemEndFunc = (item, inv, slot) => void;
inv.Expel(item) / inv.TryAcceptAll(item)
GraphUtils.TryAcceptAll(target, item, 1)
GraphUtils.CanAccept(target, item)          // 是否有格子放得下
GraphUtils.EmporiumTryAdd(item)             // 扔到店面上（兜底，别让物品无主）
```

### 3.2 窗口 / 面板

```csharp
MachineHelper.CreateMachineryWindow(true, panels)   // 原版标准机器窗口
new PixelWindow(true, "标题", WindowStyle.Machinery)
MachineHelper.Panel(label, content, role, withIntake)
new MachineryPanelRow(panels); row.SetColumns(4);
new MachineryStack.Horizontal(children) / .Vertical(children)
stack.SetAlign(MachineryStack.Align.Start)      // Start / Center / End
stack.SetMainAlign(MachineryStack.Align.Start)
stack.SetStretch(element)                       // 让某元素吃掉剩余空间
panel.SetLabel(text) / SetMinWidth(px) / SetHidden(bool)
```

`WindowStyle.Machinery = 1`。`MachineryStyle.PANEL_PAD = 4`（面板宽 = 内容宽 + 8）。

### 3.3 控件

```csharp
new MachineryButton(text, callback, width, height)   // 文字字号固定 7px
btn.SetText(s) / SetCallback(a) / SetDescription(s) / SetSize(w, h)
new MachineryText(text, size, HorizontalAlignmentOptions.Left, uppercase)
txt.SetText(s) / SetSize(n) / SetColor(c) / SetFillWidth(true) / SetStatus(label, value)
new MachineryLabel(text)                             // 高度固定 10px，适合小标签
```

### 3.4 tag 系统（**必读**）

```csharp
item.IsTag("X")              // == dict 里存在 && IsEnabled()   ← 不是"存在"！
item.GetTagReadonly("X")     // 返回 Clone；tag 不存在时会顺手创建（GetInt() == 0）
item.ModifyTag("X", st => { st.Enable(); st.SetInt(2); }, false)
```

`TagState`：`IsEnabled / SetEnabled / Enable / Disable / ToggleEnabled / GetInt / SetInt / GetString / SetString / GetFloat …`

### 3.5 常用钩子

```csharp
ModHook.OnModItemDirectoryInit              // 注册物品
ModHook.OnGameLoadedLate                     // 存档加载完
ModHook.OnPlaceInventorInventoryItemEarly    // 博士摆货「之前」
ModHook.OnPlaceInventorInventoryItemLate     // 博士摆货「之后」
item.onCycleEndSlotItemFunc                  // 每个游戏日结算
item.onOneSecondItemFunc                     // 每秒（慎用，见坑 17）
machine.canItemTargetThisFunc / onItemTargetThisFunc   // 物品扔到物品上
```

---

## 4. 踩坑清单

按「症状 → 原因 → 解法」记录，全部是真实踩过的。

### A. 工程与构建

**A1. 资源和语言表全部失效，`Mods` 里文件被摊平到根目录**
官方 `create-mod.bat` 用 `%(RecursiveDir)` 复制，吞掉了 `Textures\` 前缀。改用 `%(RelativeDir)`。

**A2. 激活被拒 / 游戏加载到旧代码**
输出目录里出现了第二个 DLL（通常是 `Assembly-CSharp.dll` 被复制进去）。所有 `<Reference>` 加 `<Private>false</Private>`，并确认 `Assemblies` 只列自己的 dll。

**A3. 本地和工坊版打架**
同 ID 两份并存，游戏随机取一份。默认不自动安装，本地调试用 `-p:InstallLocal=true`，测完删掉 `Mods\<id>` 再订阅工坊。

**A4. `dotnet build` 报 C# 语法错误，但语法没错**
`LangVersion` 被改成 `latest`，用了 Unity 运行时不支持的语法。锁死 `9.0`。

### B. 贴图与美术

**B1. 高清插画缩到 48×48 糊成一团**
不是图源问题，是物理上限：48×48 只有 2304 个像素。原版机器之所以清楚，是**照着 48×48 画的**（大色块、上亮下暗两档、深色描边、重复小元素）。要么按这个路子重画，要么把占地改成 4×4 换 64×64。

**B2. 缩图后边缘发虚**
`Image.LANCZOS` 会算出中间色。用 `Image.BOX` + 调色板量化（`quantize(colors=96)`）保硬边。

**B3. 白底去不干净，管道下方留一块白**
四角泛洪填不到「被包围的白色」。直接按阈值判背景（`min(R,G,B) >= 215 → 透明`），再做一次边缘浅色清理。

**B4. 贴图和占地对不上**
忘了 `宽 ÷ 16 = 格数`。3×3 必须 48×48。

### C. 机器 UI

**C1. 所有面板被拉成同样高，第二排怎么调都留着空白**
`MachineryPanelRow.Validate()` 会对每个面板调 `ResizePixels(w, h)`，行高取全表最大值。想各排独立高度就必须放弃表格，改用 `MachineryStack` 自由行。

**C2. 换了自由行，第二排左右各缩进一条**
两排是独立的 stack，各自按内容宽度排版后**居中**。挂载后读第一排的 `panels[i].widthPixels`，对第二排 `SetMinWidth(w)` 再 `Validate()`。

**C3. 面板内容靠左/居中留白，右边空一条**
面板内容默认居中。用 `SetMainAlign(Align.Start)` 左对齐；再用 `SetStretch(某元素)` 让它吃掉剩余宽度。

**C4. 一点按钮，整个窗口横向变大**
`MachineryText` 的宽度 = 文字自然宽度。状态文字一变长，整块控制面板被撑宽。
两处一起治：字号压小（7–8px）+ 文字按显示宽度截断（中文算 2、ASCII 算 1）；更彻底的办法是用 `SetStretch` 把它的宽度固定下来。

**C5. 按钮明明给了宽度，文字一长还是溢出**
按钮字号固定 7px，宽度由构造参数决定（`forceWidth`），文字不会撑宽面板，但会溢出按钮。按「字符数 × 7 + 留白」给宽度。

**C6. 面板上下露出两条格栅条纹**
`MachineHelper.Panel(...)` 收到**裸网格**时会自动包一层 `MachineryIntake`（自带上下通风格栅）。把网格套一层空 stack 再传进去就绕开了。

**C7. 打开机器后主线程卡死几十秒**
`onOneSecondItemFunc` 里刷 UI，每秒触发一次全窗口重排；批量搬运物品时回调又每动一次触发一次。
改成事件驱动（开窗/物品增删/每日结算）+ 文本没变就不碰 UI + 批量操作期间挂起刷新。日志里能看到 `[StallWatchdog] Main thread resumed after 60.3 s`。

**C8. 不开窗直接把物品扔到机器上，只有第一个区域能收货**
换成 `MachineryStack` 当窗口根节点后，原版依赖的「关联存储」链路断了。
两种解法：① 退回 `MachineryPanelRow`；② 让第一个区域（投放区）当总入口，接住所有物品再用 `onSlotAddItemEndFunc` 分流。

**C9. 中转区塞满了大件物品，玩家拖不进来**
投放区当中转站时，引擎先检查**中转区**放不放得下。4×4 的水罐在投放区剩不下 4×4 时就会拖不进去，哪怕目标区是空的。设计时要么把中转区做大，要么按物品尺寸预判。

### D. 物品、存储与存档

**D1. 布尔开关点了没反应（最隐蔽的一个）**
`GameItem.IsTag(name)` 的内部实现是 `return state.IsEnabled()` —— **等价于"存在且启用"**。用 `Disable()` 存 false，之后 `IsTag` 直接返回 false，读取时分不清"玩家关了"和"从没设置过"，永远回落到默认值。
解法：用 int 三态（`0 = 未设置`、`1/2 = 取值`），tag 名带 `_INT`。

**D2. 游戏报 `created with DirectoryMaster contains child items`，内容物被清空**
工厂函数不能产出带子物品的物品。所有"预置内容"（便签、初始材料）都要挪到开窗/读档后再补，并且只在槽位为空时补一次。

**D3. 改了网格尺寸，旧存档物品丢失或错位**
格子坐标是按尺寸存的。**只加不减**（3×3 → 3×4 安全，8×4 → 6×4 不安全）。必须改的话，先让玩家清空。

**D4. 物品凭空消失**
搬运途中出现 `parentInventory == null` 的"无主物品"，存档时会被丢掉。每次 `Expel` 之后都要有兜底：放不进目标区就 `EmporiumTryAdd` 扔回店面。

**D5. 装了水的瓶子被当成空瓶**
`WINE_CONTAINER_STATE == 0` 只表示"没开始发酵"，不代表瓶里没液体。判断空瓶必须同时检查液体量为 0。同理，判断"能不能当水源"要额外排除发酵中/已成品。

**D6. 可复用的工具被原版"消耗"掉**
原版对带 `REUSABLE` 标记的物品执行的是 `parentInventory.Expel` + `EmporiumTryAdd`（扔到柜台），不是销毁。用完之后要主动捞回来，否则只能用一次。

### E. 存档与钩子

**E1. 主菜单阶段拿不到存档数据**
`Init` / `OnEnable` 只做注册与订阅，存档相关逻辑放 `OnGameLoadedLate`。

**E2. 往商人货架上加货，十次有九次失败**
上架接口内部是 `TryAcceptAllOrDestroy`，要求**整块连续空位**。挂在 `Late` 时货架已被切碎，必然失败并把物品销毁。改挂 `Early`（商人开始摆货之前）先占位。

**E3. 每秒回调里做重活**
`onOneSecondItemFunc` 会在每个物品上每秒跑一次，里面做集合遍历/UI 刷新会直接把帧吃光。

### F. 创意工坊

**F1. 游戏里找不到上传入口**
`ModWorkshop` 只有订阅/查询/打开页面，**没有 Publish 接口**。只能用 SteamCMD。

**F2. 上传报 VDF 解析错误**
Valve 的 KeyValues 不支持 `//` 注释，也不能有 BOM（中文会乱码）。用无 BOM 的 UTF-8，注释一律删掉。

**F3. 更新变成了新建物品**
`publishedfileid` 填 `0` 会创建新物品。首次上传后把返回的数字填回去，之后所有更新都用它。

**F4. 上传卡住 / `CONNECT tunnel failed 502`**
本机代理问题。换一个本地代理端口，或临时关掉系统代理。`steamcmd` 对 HTTP 代理兼容性差。

**F5. 描述改了但页面没变**
Steam 创意工坊描述**只有一份，不按语言分开**；上传成功后还需刷新页面。另外 VDF 里的 description 是纯文本，BBCode（`[h1]`/`[b]`/`[hr][/hr]`）由 Steam 渲染。

**F6. 预览图变形**
工坊预览图建议 16:9（1280×720 足够），上限 1 MB。模组菜单用的 `preview.png` 是另一张（正方形），别混用。

---

## 5. 调试流程

### 5.1 看日志

```powershell
$log = "$env:USERPROFILE\AppData\LocalLow\Questing Goose Studio\Probably Stolen\Player.log"
```

| 关键字 | 含义 |
|---|---|
| `[StallWatchdog] Main thread resumed after …` | 主线程卡死，找卡死前最后几条日志 |
| `contains child items` | 工厂产出了带子物品的物品（坑 D2） |
| `Localization key 'X' not found` | 语言表缺 key |
| `Destroyed unplaced items` | 物品放不下被销毁（坑 E2/D4） |
| `Exception` / `NullReference` | 自己的代码抛异常，注意看命名空间 |
| `window untitled` | 创建了没有标题的窗口 |

自己的 mod 名会以 `<Name>` 里的中文出现在日志里，搜中文名比搜 ID 快。

### 5.2 二分定位

卡死/崩溃先判断「是不是我的 mod」：禁用 mod 跑一遍，再启用跑一遍。日志里 mod 自己的输出都有前缀，能直接看出最后一条是谁打的。

### 5.3 不要靠猜 API

签名和行为一律用 `psd.py` 确认。这个游戏有大量「名字和行为不符」的地方（`IsTag`、`CanAccept`、`TryAcceptAllOrDestroy`）。

---

## 6. 发布到创意工坊

```powershell
# 1. 打内容包：manifest + dll + preview + Textures + Localization 拷到 dist\workshop\
# 2. 写 VDF（无 BOM、无注释）
# 3. 上传
& "E:\steamcmd\steamcmd.exe" +login +workshop_build_item "D:\...\dist\workshop_build.vdf" +quit
```

VDF 关键字段：

```
"workshopitem"
{
    "appid"            "4348910"
    "publishedfileid"  "3836042396"     // 首次填 0，成功后回填
    "contentfolder"    "...\dist\workshop"
    "previewfile"      "...\dist\preview_16x9.png"
    "visibility"       "2"              // 2 私有 / 0 公开
    "title"            "..."
    "description"      "..."
    "changenote"       "..."
}
```

成功标志：`Success. Published file ID: 3xxxxxxxxxx`。

更新流程：改 `<ModVersion>` → build → 同步 `dist\workshop` → 改 `changenote` → 重新上传（`publishedfileid` 保持原值）。

---

## 7. 现有实现参考

`Modding/BrewingConvenience`（酿酒便利化 v1.1.0）是一个完整样例，覆盖了：

| 想看什么 | 看哪个文件 |
|---|---|
| 物品注册、博士上架、调试按钮 | `ModEntry.cs` |
| 机器工厂：8 个区域 + 9 块面板 + 布局 | `BrewerMachine.cs` |
| 业务逻辑：分拣 / 配比 / 开酿 / 状态 | `BrewerUnits.cs` |
| 原版酿酒规则（tag 名、水质档位、品质阈值） | `WineTags.cs` |
| 本地化封装 | `Loc.cs` |
| 语言表 | `Localization/zh.csv`、`en.csv` |

配套的深度调研在 `docs/research/`：

- `brewing-api.md` — 酿酒系统全流程（发酵、分值、品质、每日结算）
- `container-ui-api.md` — 容器/网格/窗口/面板/拖放
- `inventor-api.md` — 博士商店、上架、人脉升级
- `water-api.md` — 液体容器、水质、倒水

---

## 8. 一套可复用的开工顺序

1. `python psd.py types "关键词"` 找到相关类型
2. `sig` 看成员、`il` 看关键方法语义
3. 建工程（抄第 2 节模板）→ 先做**最小可运行版本**（能激活、能在游戏里出现）
4. 每加一个功能就 build + 进游戏验证，不要攒着
5. 涉及存档数据时，先想清楚「老存档没有这个字段会怎样」
6. 涉及 UI 时，先想清楚「内容变长/变多会怎样」
7. 发布前用 `-p:InstallLocal=true` 本地跑一遍，再删掉本地副本、上传工坊
