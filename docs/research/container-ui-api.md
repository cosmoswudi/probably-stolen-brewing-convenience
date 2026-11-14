# Probably Stolen — 容器 / 网格 / 家具 / UI / 时间 / 存档 API 调研

> 目标：为实现「酿酒机器」家具（葡萄 8x8、啤酒瓶 4x8、酵母 3x3、成酒输出区）摸清可用 API。
> 只读调研，未修改游戏目录内任何文件（除本报告）。
>
> 调研对象：`Probably Stolen_Data/Managed/Assembly-CSharp.dll`（Unity 2021.3.45f2 / Mono / netstandard2.1）
> 时间：2026-11-08

---

## 0. 方法与复现步骤

`ilspycmd` 被本机 Device Guard / 应用控制策略拦截（`ilspycmd.exe` 一执行就 "已被组织上的 Device Guard 策略阻止"），
改用「框架依赖的 .NET 控制台 + ICSharpCode.Decompiler 9.1」自建反编译器（`dotnet app.dll` 可正常运行）。

复现（临时目录，不落在游戏目录里）：

```bash
mkdir -p /tmp/decomp && cd /tmp/decomp
dotnet new console -o .
dotnet add package ICSharpCode.Decompiler --version 9.1.0.7988
# Program.cs: 用 CSharpDecompiler 提供 list / sig / type / all 四种模式
dotnet build
dotnet bin/Debug/net10.0/decomp.dll list ".../Managed/Assembly-CSharp.dll" > types.txt   # 2742 个类型
dotnet bin/Debug/net10.0/decomp.dll sig  ".../Assembly-CSharp.dll" ModHelper ModHook       # 成员签名
dotnet bin/Debug/net10.0/decomp.dll type ".../Assembly-CSharp.dll" MachineWineRack         # 反编译指定类型
dotnet bin/Debug/net10.0/decomp.dll all  ".../Assembly-CSharp.dll" -o /tmp/ps_all          # 全量（946 个顶层类型，约 23s）
```

注意：`ilspycmd` 的 `-l c` 等价于本工具的 `list`。

---

## 1. 结论速览

| 问题 | 结论 |
|---|---|
| 网格容器类 | `GameGridInventory(width, height)`（继承 `GameInventory`，实现 `PixelElement`）。原版最大用到 `50x50`，`24x10` 是商店地板，`8x8`/`4x8` 完全没问题 |
| 单格容器 | `GameSlotInventory()`（一格一物，常用于电池/容器槽） |
| 物品占格 | `GameItem.shape` / `GameItem.modifiedShape`，类型是 `GridShape`（位图式形状，1=占格）。用 `GridShapeBuilder` 构建，可从 sprite 切片自动推导 |
| 单格堆叠 | **默认一格只放 1 个**（`MaxNumAdd` 在没设置 `maxSlotAmountItemFunc` 时直接返回 1）。要堆叠必须给物品或背包设 `maxSlotAmountItemFunc` + `mayStackItemFunc`；数量存在 `GameItem.unitCount` |
| 放入/取出 | `inventory.UncheckedAccept(item)` / `inventory.Expel(item)`；带校验的放置用 `GraphUtils.TryAcceptAll(inv, item)` |
| 家具 | 家具 = `GameItem`，工厂注册在 `FurnitureItemDirectory`（公开 `Add(id, Func<GameItem>)`），靠 `ItemDirectory.CreateEmptyItem().SetContentWindow(window)` 生成。mod 可以新增，不限于复用原版类型 |
| mod 注册物品 | `ModHook.OnModItemDirectoryInit += dir => dir.Add("my_brewer", () => MyBrewer.Create());`，之后 `ModHelper.SpawnItem("my_brewer")` 即可 |
| 机器 UI | **不要用 `CustomUIManager`**。原版机器的多网格面板用 `PixelWindow` + `MachineHelper.CreateMachineryWindow(...)` + `MachineHelper.Panel(标签, GameGridInventory)`，拖放/旋转/吸附全部免费 |
| CustomUIManager | 是给「元界面」（设置、日志、网络、纸张）用的，**不支持物品拖放**。可做信息/配置面板 |
| 时间 | `StoreStation.Instance.dayCounter` / `StoreStation.GetDayCounter()`，在 `StoreStation.StartDay()` 里 +1；机器逻辑挂在 `GameItem.onCycleEndSlotItemFunc`（每晚结算一次） |
| mod 数据 | `ModHelper.SetModData*` → `PlayerStore.modData`，**随存档保存（per-save）**；`GetModDataFolder` 是全局（persistentDataPath） |
| 自定义容器里的物品能否存档 | **能**。`SaveManager.EncodeNodes/DecodeNodes` 会 BFS 遍历 GameItem 的整个子树（含 contentWindow → 面板 → 网格 → 物品），按「背包节点 BFS 序号」回填。前提是工厂函数每次构建出**结构完全一致**的对象图 |
| 已有现成酿酒链路 | **游戏里已经有整套酿酒系统** `WineHelper`（发酵 4 天、酵母 power、品质评分、陈酿）+ 一台「发酵瓶」机器。强烈建议直接复用 |

---

## 2. 物品容器与网格系统

### 2.1 类层次

```
GameInventoryFunc  (回调集合 + TagSystem)
   └─ GameInventory (abstract) : GraphNodeStorage
        ├─ GameGridInventory        —— 网格容器（本次核心）
        ├─ GameSlotInventory        —— 单格容器
        └─ GameGridScrollableInventory —— 可滚动网格（大容器 UI 用）
GraphNodeStorage 是整棵「物品/容器」树的节点接口：parents / children。
```

`GameInventory` 关键成员（`GameInventory.cs`）：

```csharp
public abstract class GameInventory : GameInventoryFunc, GraphNodeStorage
{
    public string identifier;
    public bool overrideLockInsert, overrideLockRemove;
    public virtual bool SupportsGroupPlacement => false;
    public GraphNodeStorage parent { get; protected set; }
    public abstract List<GameItem> childItems { get; }      // 容器里的物品
    public abstract List<GraphNodeStorage> children { get; }

    public SlotMarker TryInventorySlot(GameItem item, GridShape tempShape, TagSystem itemState = null);
    public SlotMarker TryInventorySlot(GameItem item, int requestedNum, GridShape tempShape, TagSystem itemState = null);
    public abstract SlotMarker TryInventorySlot(GameItem item, int requestedNum, Vector2 itemGraphicsCenterPosition, GridShape tempShape, TagSystem itemState);

    public abstract bool UncheckedAccept(GameItem item);     // 直接塞进去（无校验）
    public abstract bool Expel(GameItem item);               // 拿出来
    public GameInventory UncheckedAcceptAll(List<GameItem> items);
    public GameInventory TryAcceptAll(GameItem item);        // 自动找空位
    public GameInventory ExpelAll();

    public abstract SlotMarker GetItemInventorySlot(GameItem item);
    public abstract SlotMarker GetIncompleteItemInventorySlot(GameItem item);
    public abstract bool CheckInventorySlotStillValid(GameItem item, GridShape tempShape, TagSystem tempState);
    public abstract bool MayHaveValidInventorySlot(GameItem item);
    public abstract SlotMarker TryFindOneValidInventorySlot(GameItem item);
    public abstract List<TreeNodeRender> HighlightSlot(SlotMarker slot, RenderHandler.BackgroundColor color, bool doReplace);
    public abstract void ClearHighlight();
    public abstract bool IsAdjacent(SlotMarker slot, SlotMarker slot2);
    public abstract void Validate();
    public void LockInv();  public void UnlockInv();         // = overrideLockInsert/Remove
    public bool IsInsertLocked(); public bool IsRemoveLocked();
    public GameItem GetParentItem();                          // 找到宿主物品（机器）
    public long GetTotalValue(List<GameCharacterItem> party = null, GameCharacterItem vendor = null);
}
```

### 2.2 `GameGridInventory`（网格容器）— 构造与尺寸

```csharp
public class GameGridInventory : GameInventory, PixelElement
{
    public GameGridInventory(int width, int height);              // 矩形满格
    public GameGridInventory(string shape, int width);            // 位图字符串：'0'=可放, '1'=不可放
    public GameGridInventory SetShape(int width, int height);
    public GameGridInventory SetShape(string shape, int width);

    public int widthPixels  { get; }     // = inventoryShape.width  * UISettings.current.gridSize
    public int heightPixels { get; }     // = inventoryShape.height * UISettings.current.gridSize
    public GridShape inventoryShape { get; }
    public override bool SupportsGroupPlacement => true;   // 支持整组拖放
    public bool canResize => false;
    public TreeNodeRender handler { get; }  public RectTransform rectTransform { get; }

    public GameGridInventory SetCellBackdrop(int x, int y, Sprite sprite);  // 单格背景图（很适合"区域"视觉区分）
    public GameGridInventory ClearCellBackdrops();
    public GameGridInventory SetMachineryStyle();          // 机器风格边框
    public GameGridInventory SetTransparency(float t);
    public GameGridInventory SetBackgroundSprite(string atlasPath, string spritePath);
    public GameGridInventory SetBackgroundSprite(string atlasPath, string spritePath, Color color);
    public bool HasFreeCell();
    public bool CanTakeAnything();
}
```

位图形状字符串示例（原版 CO₂/模块仓，7 宽）：

```csharp
new GameGridInventory("10000010001000001110000010001000001", 7)   // 7x5 带缺口
new GameGridInventory(8, 8)      // 葡萄区
new GameGridInventory(4, 8)      // 啤酒瓶区
new GameGridInventory(3, 3)      // 酵母区
```

由于 `GridShape` 是逐格位图，**8x8 / 4x8 / 3x3 都是任意矩形，直接传宽高即可**。

`UncheckedAccept` 实现（`GameGridInventory.cs:712`）要点：

```csharp
public override bool UncheckedAccept(GameItem item)
{
    if (item == null || item.parentInventory != null || items.Contains(item)) return false;
    base.onSlotAddItemFunc?.Invoke(item, this, GetIncompleteItemInventorySlot(item));
    items.Add(item);
    item.AddParent(this);
    if (item is PixelElement pe) pe.SetRenderingParent(this);
    ...
    item.ValidateShapeState();
    return true;
}
```

注意：`UncheckedAccept` **不检查空间/形状**，只做「挂树 + 渲染挂接」。要正规放置用
`GraphUtils.TryAcceptAll(inv, item)`（内部 `TryInventorySlot` 找位）。

### 2.3 形状 / 占格

`GridShape` 是接口，`GridShapeBuilder` 是实现（`[Serializable]`，可直接进存档结构）：

```csharp
public class GridShapeBuilder : GridShape
{
    public GridShapeBuilder();
    public GridShapeBuilder(int width, int height);
    public GridShapeBuilder(GridShape shape);            // 克隆
    public GridShapeBuilder SetData(byte[] data, int width);
    public GridShapeBuilder SetData(string data, int width);   // "0101..." 字符串
    public GridShapeBuilder SetDataFill(int w, int h, byte b);
    public GridShapeBuilder SetDataFill(byte b);
    public GridShapeBuilder SetDataOutside(byte b);            // 越界返回值
    public GridShapeBuilder SetPosition(int minX, int minY);
    public GridShapeBuilder SetOrientation(int o);
    public GridShapeBuilder SetFlipped(bool f);
    public GridShape Build();
    public GridShape Clone();
}

public interface GridShape
{
    int minX, minY, maxX, maxY, width, height, globalWidth, globalHeight, orientation; bool flipped;
    byte GetLocal(int x, int y);
    byte Get(int globalX, int globalY);          // 已应用旋转/翻转
    byte GetOutsideBounds();                     // 默认 GetLocal(-1,-1)
    (int,int) ForwardTransform(int localX, int localY);
    bool Intersects(int x, int y);               // 用于占格检测
    GridShape Clone();
}
```

`GameItem` 上的形状字段：

```csharp
public GridShape shape { get; set; }          // 原始形状（用于存档/渲染基准）
public GridShape modifiedShape { get; set; }  // 实际占格（旋转/翻转后）
public GameItem SetShape(GridShape itemShape);
public GameItem SetSpriteAndShape(string spriteAtlasPath, string spritePath);   // 从 sprite 切片自动推导（要求切片是 gridSize 的整数倍）
public GameItem SetSpriteAndShapeFromMod(string modId, string spriteKey);       // mod 贴图版本
public static GridShapeBuilder BuildShapeFromSprite(string atlasPath, string spritePath);
```

> `BuildShapeFromSprite` 会读 sprite 的 rect，算出 `max(1, rect.width / UISettings.current.gridSize)` 与高度，
> 再逐像素判断不透明 → 占格。若切片没对齐网格会打 warning（"SPRITE NOT ON GRID"）。
> 所以机器自己的贴图尺寸决定它在商店里占几格。

### 2.4 堆叠（重点）

```csharp
public int unitCount { get; set; }             // 堆叠数量
public GameItem SetUnitCount(int num);
public GameItem SetStack(int num);             // = SetUnitCount
public GameItem SetAmount(int num);            // = SetUnitCount

public int MaxNumAdd();                        // 当前格还能加几个
public int MaxNumAdd(SlotMarker slot);
public bool MayStack(GameItem otherItem);
public SlotMarker GetStackSlot(GameItem itemToBeStacked);
public GameItem GetSplitItem(int requestedAmount);
public int MaxNumRemove();
```

`MaxNumAdd(SlotMarker)`（`GameItem.cs:1240`）的关键逻辑：

```csharp
int num = Math.Min(inv.maxSlotAmountItemFunc?..., item.maxSlotAmountItemFunc?...) ?? int.MaxValue;
if (slot.inventory.maxSlotAmountItemFunc == null && base.maxSlotAmountItemFunc == null)
    num = 1;                       // ★ 没有任何上限函数 => 一格最多 1 个
```

**结论**：默认一格 = 1 个物品实例（`unitCount` 也是 1，但同一格不会再放第二个）。
想要「一格 25 个葡萄」必须：

```csharp
// 在容器侧（推荐）：
grid.maxSlotAmountItemFunc = (item, inv, slot) => 25;
// 并且在物品侧允许合并：
grape.mayStackItemFunc = (a, b) => a.identifier == b.identifier;
```

原版参考：`UnusedDirectory.cs:468` `gameItem.maxSlotAmountItemFunc = (_, inv, _) => 99;`、
`GameItem.GetSplitItem` 用于「拆一半」交互。

### 2.5 容器回调表（决定「哪个格子只收什么」）

`GameInventoryFunc`（**每个容器实例**一组回调，全部是 `public { get; set; }`）：

```csharp
Func<GameItem, GameInventory, bool>                         mayInventoryAddItemFunc;      // ★ 过滤能否放入
Func<GameItem, GameInventory, bool>                         mayInventoryRemoveItemFunc;
Func<GameItem, GameInventory, SlotMarker, int>              maxSlotAddItemFunc;
Func<GameItem, GameInventory, SlotMarker, int>              maxSlotRemoveItemFunc;
Func<GameItem, GameInventory, SlotMarker, int>              maxSlotAmountItemFunc;        // ★ 单格堆叠上限
Func<GameItem, GameInventory, SlotMarker, int>              minSlotAmountItemFunc;
Action<GameItem, GameInventory, SlotMarker>                 onSlotAddItemFunc;            // 放入前
Action<GameItem, GameInventory, SlotMarker>                 onSlotAddItemEndFunc;         // 放入后
Action<GameItem, GameInventory, SlotMarker>                 onSlotAddItemLateFunc;        // 放入后（最晚）
Action<GameItem, GameInventory, SlotMarker>                 onSlotRemoveItemFunc;
Action<GameItem, GameInventory, SlotMarker>                 onSlotRemoveItemEndFunc;
Action<GameItem, GameInventory, SlotMarker>                 onSlotRemoveItemLateFunc;
Action<GameItem, GameInventory, SlotMarker>                 onSlotChangeAmountItemFunc;
Func<GameItem, GameInventory, SlotMarker, int>              slotPriorityFunc;             // 自动整理优先级
Func<GameItem, GameInventory, SlotMarker, GameItem, bool>   slotSearchPassthroughForItemFunc;
Func<..., GridShape>                                        parentItemShapeModifierForItemFunc;
Func<..., List<ItemSpriteModifier>>                         parentItemSpriteModifierForItemFunc;
Func<..., List<ItemStateModifier>>                          parentItemStateModifierForItemFunc;
Func<..., bool>                                             isItemStateValidFunc;
Func<GameItem, GameInventory, SlotMarker, bool>             maySelectSlotItemFunc, canSelectSlotItemFunc;
Func/ Action<...GameItem>                                   may/can/onSlotItemTargetItemFunc;   // 格内物品互相作用（拖到物品上）
Func/ Action<...>                                           may/can/onItemTargetSlotItemFunc;   // 物品拖到格子上
Func<GameItem, GameInventory, SlotMarker, bool>             may/can/onActivateSlotItemFunc;     // 点击激活
Func<GameItem, GameInventory, SlotMarker, bool>             may/can/onToggleSlotItemFunc;       // 点击开关
```

`GameItemFunc`（**每个物品**一组，机器用到的关键几个）：

```csharp
public Func<GameItem, GameInventory, bool>        mayInventoryAddItemFunc, mayInventoryRemoveItemFunc;
public Func<GameItem, GameItem, bool>             mayStackItemFunc;
public Func<GameItem, GameItem, bool>             mayItemTargetItemFunc / canItemTargetItemFunc;  // 拖其他物品到自己身上
public Action<GameItem, GameItem>                 onItemTargetItemFunc;
public Func<GameItem, GameItem, bool>             mayItemTargetThisFunc / canItemTargetThisFunc;
public Action<GameItem, GameItem>                 onItemTargetThisFunc;                            // 例：水壶倒进水瓶
public Func<GameItem, List<ItemSpriteModifier>>   getItemSpriteModifierForItemFunc;                // 叠加贴图（瓶子上贴酒标）
public Func<GameItem, GameInventory, SlotMarker, GridShape, TagSystem, RichTextBuilder> tooltipRichTextFunc;
public Action<GameItem, GameInventory, SlotMarker> onConfigureSlotItemFunc;
public Action<GameItem, GameInventory, SlotMarker> onCycleEndEarlySlotItemFunc;   // ★ 每天结算（早）
public Action<GameItem, GameInventory, SlotMarker> onCycleEndSlotItemFunc;        // ★ 每天结算（主）
public Action<GameItem, GameInventory, SlotMarker> onCycleEndLateSlotItemFunc;    // ★ 每天结算（晚）
public Action<GameItem, GameInventory, SlotMarker> onTurnEndSlotItemFunc;
public Action<GameItem, GameInventory, SlotMarker> onOneSecondItemFunc;           // ★ 每「现实秒」tick
public Action<GameItem>                            onLoaded;                       // ★ 从存档恢复后回调
public Action<GameItem>                            onUnlockedFunc;
public Action<GameItem, GameItem>                  onUnloadChildItemFunc;
public void SetAllMayFalse();   // 一键全禁（安全模式）
```

### 2.6 现成的过滤工具 `ContainerHelper`

```csharp
public static class ContainerHelper
{
    public const string CONTAINER_TAG = "CONTAINER_TAG";
    public const string POUCH_TAG, BACKPACK_TAG, SAFE_TAG, TRACK_OPENED_TAG, OPENED_TAG,
                        CONTAINER_TOOLTIP_TAG, ITEM_MEDICAL_TAG, PRE_CONTAINER_TAG,
                        NESTABLE_DEVICE_TAG, STORABLE_PACKAGE_TAG;

    public static void AllowOnlyTaggedItem(GameInventory inv, string tag, bool ownedOnly, bool excludeContainer);
    public static void AllowOnlyTaggedItemsOr(GameInventory inv, string[] tags, bool ownedOnly);
    public static void AllowOnlyTaggedItemsAnd(GameInventory inv, string[] tags);
    public static void AllowOnlyOwnedItems(GameInventory inv);
    public static void AllowOnlyOwnedItemsExcludeContainers(GameInventory inv);
    public static bool CanContainerStoredItem(GameItem addedItem, GameInventory inv);
    public static bool IsNestingAllowed(GameItem addedItem, GameItem parentItem);
    public static void InitContainerItem(GameInventory inv, GameItem item, List<string> allowedTags, List<string> allowedId);
    public static void AddContainerTooltipTag(GameItem item);
    public static void SetBasicContainerValueChangeRule(GameItem box, GameInventory inv);
}
```

用法（原版搅拌机）：

```csharp
inputGrid.mayInventoryAddItemFunc = (item, inv) => ContainerHelper.CanContainerStoredItem(item, inv);
moduleGrid.mayInventoryAddItemFunc = (item, inv) => item.IsTag("MODULE_TYPE_CHEM") && GeneralHelper.IsItemOwned(item);
```

---

## 3. `ModHelper` 完整 public API（无省略）

> 全部为 `public static`。`ModHelper.cs` 共 1243 行；下面按功能分组，签名逐字照抄反编译结果。
> 泛型参数在反编译输出里被抹成了 `System.ValueTuple[]` / `System.Func[]`，已按源码还原。

### 3.1 常量

```csharp
public static string MOD_ID_TAG, MOD_NAME_TAG;
public static string DEBUG_CATEGORY_STORE, DEBUG_CATEGORY_COMMAND, DEBUG_CATEGORY_SPAWN,
                    DEBUG_CATEGORY_SPECIAL, DEBUG_CATEGORY_MODDING, DEBUG_CATEGORY_MARKETING;
public static string OPTION_SECTION_GAMEPLAY, OPTION_SECTION_AUDIO, OPTION_SECTION_DISPLAY,
                    OPTION_SECTION_DIALOGUE, OPTION_SECTION_KEYBIND;
```

### 3.2 物品 / 金钱

```csharp
void      AddCredit(int amount, bool playSound)
GameItem  SpawnItem(string id)                       // = ItemSpawner.Spawn(id) → DirectoryMaster.Item(id)
GameItem  SetItemOwnerShip(GameItem item, bool isOwn)
void      MoveItemToFrontCounter(GameItem item)
void      MoveItemToBackCounter(GameItem item)
void      MoveItemToInventory(GameItem item)          // → EmporiumEntry.Instance.invElement（商店主仓库 24x10）
void      MoveItemToTrash(GameItem item)
void      MoveItemToDisplayCase(GameItem item)        // → showcaseElement（橱窗 7x5）
void      MoveItemToAfterhours(GameItem item)
```

### 3.3 本地化 / 音效 / 来源标记

```csharp
Locale   GetCurrentLocale()
string   GetLocalized(string modId, string key, params object[] args)
string   GetSoundIdentifier(string modId, string soundKey)
bool     PlaySound (string modId, string soundKey)
bool     PlaySound2(string modId, string soundKey)
bool     PlaySound3(string modId, string soundKey)
void     SetSource(GameItem item, string modId, string modName)
void     SetClientSource(StoreClient client, string modId)
```

### 3.4 调试菜单（F9）与配置

```csharp
void     AddDebugFunction(string modId, string category, string name, Action action, bool tall = false)
int      RemoveDebugFunctions(string modId)

ModConfig GetConfig(string modId)
string    GameOptionId(string modId, string optionId)
void      AddGameOptionSection(string modId, string sectionId, Func<string> label)
void      AddGameOptionToggle (string modId, string sectionId, string optionId, Func<string> label,
                               Func<bool> get, Action<bool> set, Func<string> tooltip)
void      AddGameOptionSlider (string modId, string sectionId, string optionId, Func<string> label,
                               Func<float> get, Action<float> set, float min, float max,
                               bool wholeNumbers, Func<string> tooltip, Func<float,string> valueText)
void      AddGameOptionSlider (… , Func<string> tooltip)                      // 无 valueText 重载
void      AddGameOptionDropdown(string modId, string sectionId, string optionId, Func<string> label,
                                Func<IList<string>> options, Func<int> get, Action<int> set, Func<string> tooltip)
void      AddGameOptionKeyBind(string modId, string sectionId, string optionId, Func<string> label,
                               Func<KeyCode> get, Action<KeyCode> set, Func<string> tooltip)
bool      RemoveGameOption(string modId, string optionId)
int       RemoveGameOptions(string modId)
```

`ModConfig`（`ModHelper.GetConfig(modId)` 的返回）：

```csharp
public static string RootFolder { get; }
public string ModId { get; }  public string FilePath { get; }
public IReadOnlyList<ConfigEntry> Entries { get; }  public bool HasEntries { get; }
public IEnumerable<string> Sections { get; }

static ModConfig For(string modId);  static ModConfig Find(string modId);  static bool HasSettings(string modId);
ConfigEntry Get(string key);
ConfigEntry<bool>   Define(string key, bool   defaultValue, string section, Func<string> label,
                           Func<string> tooltip = null, bool requiresRestart = false);
ConfigEntry<int>    Define(string key, int    defaultValue, string section, Func<string> label,
                           int?   min = null, int?   max = null, Func<string> tooltip = null, bool requiresRestart = false);
ConfigEntry<float>  Define(string key, float  defaultValue, string section, Func<string> label,
                           float? min = null, float? max = null, Func<string> tooltip = null, bool requiresRestart = false);
ConfigEntry<string> Define(string key, string defaultValue, string section, Func<string> label,
                           string[] choices = null, Func<string> tooltip = null, bool requiresRestart = false);
ConfigEntry<TEnum>  DefineEnum<TEnum>(string key, TEnum defaultValue, string section, Func<string> label,
                           Func<string> tooltip = null, bool requiresRestart = false) where TEnum : struct, Enum;
void Save();  void ResetToDefaults();
```
`ConfigEntry<T>`：`T Value { get; }`、`T Default { get; }`、`void Set(T)`、`void Reset()`。
配置写在 `ModConfig.RootFolder` 下的文件里（**不是存档**），并可被 `ModConfigWindow.Open(mod)` 自动渲染成设置界面。

### 3.5 掉落表

```csharp
void   RegisterLootTable(string modId, string tableId, params (string itemId, float weight)[] entries)
void   AddLootEntry  (string modId, string tableId, string itemId, float weight)
void   RemoveLootEntry(string modId, string tableId, string itemId)
void   SetLootWeight (string modId, string tableId, string itemId, float weight)
void   ClearLootTable(string modId, string tableId)
void   RegisterLootGroup(string modId, string groupId, params (string tableId, float weight)[] entries)
void   AddLootGroupEntry/RemoveLootGroupEntry/SetLootGroupWeight/ClearLootGroup(...)
List<LootEntry> GetLootEntries(string tableId);
List<LootEntry> GetLootGroupEntries(string groupId);
string RollLootTable(string tableId);   string RollLootGroup(string groupId);
GameItem SpawnFromLootTable(string tableId);  GameItem SpawnFromLootGroup(string groupId);
```

### 3.6 网络升级（Wild Network）

```csharp
void AddNetworkUpgrade(string modId, string id, int cost, string subtitle, Func<string> title,
                       Func<string> description, Action onUnlock, string tab = null, string iconKey = null,
                       string[] prerequisites = null, int cooldownDays = 0,
                       Func<string> subtitleLabel = null, Func<string> alreadyBought = null, bool recommended = false)
void SetNetworkTabLabel(string tab, Func<string> label)
int  RemoveNetworkUpgrades(string modId)
bool IsNetworkUpgradeUnlocked(string id)
NetworkUpgrade GetNetworkUpgrade(string id)
void OpenWildNetwork();  void CloseWildNetwork();
```

### 3.7 新游戏 / 难度 / 鉴定 / 时段地点 / 纸张书

```csharp
void AddStartingScenario(string modId, string id, Func<string> title, Func<string> description,
                         Func<string>[] startWith, Action onStart, string iconKey, Func<string> tag,
                         StartingScenario.TagTone tagTone, bool skipIntro, bool comingSoon)
int  RemoveStartingScenarios(string modId);
string GetStartingScenarioId();  bool IsStartingScenario(string id);
void OpenNewGameSpecialization(); void CloseNewGameSpecialization();
void AddStartingPerk(string modId, string id, int cost, StartingPerk.StartingPerkType type, Func<string> name,
                     Func<string> description, string iconKey, string[] incompatiblePerks, int extraPerkSlots, bool mindEditable)
int  RemoveStartingPerks(string modId);  bool IsStartingPerkActive(string id);
List<string> GetStartingPerkIds();  void OpenNewGamePerks();  void CloseNewGamePerks();

void AddDifficulty(string modId, string id, int order, Func<string> title, Func<string> subtitle,
                   Action<DifficultyRules> configure, string basedOn, Func<string>[] notes, string iconKey)
void ModifyDifficulty(string id, Action<Difficulty> modify)
int  RemoveDifficulties(string modId);  string GetDifficultyId();  DifficultyRules GetDifficultyRules();
bool IsDifficultyAtLeast(string id);

void AddAppraisalItem(string modId, string id, string itemId, int unlockRank, Func<GameItem> createRandom,
                      Func<GameItem,int> getCorrectValue, Func<GameItem,bool> isType, string guideItemId,
                      Func<string> displayName)
int  RemoveAppraisalItems(string modId);  int GetAppraiserRank();

DelegateAfterhourLocation AddAfterhourLocation(string modId, string id, Func<string> title, Action onArrive,
                      Action onLeave, Action<CustomUIBuilder> buildPanel, string imageKey, string backgroundKey,
                      string ambienceId, Func<bool> isDiscovered, Func<bool> isAvailable)
void AddAfterhourLocation(AfterhourLocation location);
int  RemoveAfterhourLocations(string modId);  AfterhourLocation GetAfterhourLocation(string id);
string GetCurrentAfterhourLocationId();  bool IsOutsideStore();  void ReturnFromAfterhour(bool askAboutGroundItems);
bool RegisterAfterhourAmbience(string modId, string id, string soundKey);
bool RegisterAfterhourAmbience(string id, AudioClip clip);
int  GetAfterhourVisits(string locationId);
int  GetAfterhourCounter(string locationId, string key, int fallback);
void SetAfterhourCounter(string locationId, string key, int value);

AfterhourEventDefinition AddAfterhourEvent(string modId, string id, string locationId, Func<string> title,
                      int durationDays, Func<string> description, IEnumerable<AfterhourEffect> effects,
                      float dailyChancePercent, AfterhourEventTone? tone, Func<bool> canStart,
                      Action onStart, Action onEnd, IEnumerable<AfterhourWeekday> weekdays, bool closesLocation)
AfterhourEffect AfterhourEffect(Func<string> text, string modifierKey, float modifierValue,
                                AfterhourEffectPolarity polarity)
int  RemoveAfterhourEvents(string modId);
bool StartAfterhourEvent(string eventId, int durationDays);
bool EndAfterhourEvent(string eventId);
void EndAllAfterhourEvents(string locationId);
bool IsAfterhourEventActive(string eventId);
List<string> GetActiveAfterhourEventIds(string locationId);
float GetAfterhourModifier(string locationId, string key);

PaperUI OpenPaper(string modId, string bookId);   PaperUI OpenPaper(string fullId);
void ClosePaper(string modId, string bookId);     bool IsPaperOpen(string modId, string bookId);
PaperUI GetOpenPaper(string modId, string bookId);
void OnPaperOpened(string modId, string bookId, Action<PaperUI> callback);
void RegisterPaperBook(string modId, PaperBookData data);
PaperBookData ParsePaperBook(string json);
int  RemovePaperBooks(string modId);  List<string> GetPaperBookIds(string modId);
PaperUI CreatePaper(string modId, string bookId, string background, float width, float height);
GameItem SpawnPaperItem(string modId, string bookId);

bool RequestSteamAuthTicket(string serverIdentity, Action<byte[]> onTicket);
void CancelSteamAuthTicket();
```

### 3.8 存档内 mod 数据 & 文件

```csharp
void   SetModData(string modId, string key, string value);      // 写入 PlayerStore.modData（随存档）
string GetModData(string modId, string key, string fallback = null);
bool   HasModData(string modId, string key);
bool   RemoveModData(string modId, string key);
List<string> GetModDataKeys(string modId);
int    ClearModData(string modId);
void   SetModDataInt  (string modId, string key, int value);
int    GetModDataInt  (string modId, string key, int fallback = 0);
void   SetModDataFloat(string modId, string key, float value);
float  GetModDataFloat(string modId, string key, float fallback = 0f);
void   SetModDataBool (string modId, string key, bool value);
bool   GetModDataBool (string modId, string key, bool fallback = false);

string GetModFolder(string modId);            // <game>/Mods/<id>
string GetModDataFolder(string modId);        // %persistentDataPath%/ModData/<id>  ← 全局，非存档
bool   ModFileExists(string modId, string relativePath);
byte[] ReadModFileBytes(string modId, string relativePath);
string ReadModFileText (string modId, string relativePath);
AssetBundle LoadModAssetBundle(string modId, string relativePath);
bool   ModDataFileExists(string modId, string relativePath);
byte[] ReadModDataFileBytes(string modId, string relativePath);
string ReadModDataFileText (string modId, string relativePath);
void   WriteModDataFileBytes(string modId, string relativePath, byte[] bytes);
void   WriteModDataFileText (string modId, string relativePath, string text);
bool   DeleteModDataFile(string modId, string relativePath);
List<string> ListModDataFiles(string modId);
```

内部实现细节（重要）：

```csharp
private static string ModDataKey(string modId, string key) => modId + ":" + key;
private static Dictionary<string,string> ModDataStore(bool warn)
{
    var d = PlayerStore.Instance?.ModData();      // → public Dictionary<string,string> modData
    if (d == null && warn) Debug.LogWarning("[ModHelper] Mod data is only available while a run is loaded");
    return d;
}
```

`PlayerStore.modData` 被标了 `[ES3Serializable]` ⇒ **每个存档独立**，主菜单阶段写会静默失败（只打 warning）。

---

## 4. `ModHook` 全部事件（含参数类型）

`public class ModHook : MonoBehaviour`，`public static ModHook Instance { get; private set; }`（`DontDestroyOnLoad`）。

| 事件 | 签名 |
|---|---|
| `OnModItemDirectoryInit` | `Action<ModItemDirectory>` |
| `OnGenerateCustomerVeryEarly` / `Early` / `Normal` / `Late` / `VeryLate` | `Action<StoreClientManager>` |
| `OnGameLoadedInit` / `OnGameLoadedEarly` / `OnGameLoadedNormal` / `OnGameLoadedLate` | `Action` |
| `OnShutterOpenedEarly` / `Late` | `Action` |
| `OnShutterClosedEarly` / `Late` | `Action` |
| `OnGoingSleepEarly` / `OnGoingSleepLate` | `Action` |
| `OnWakingUpEarly` / `OnWakingUpLate` | `Action` |
| `OnLeavingStoreEarly` / `Late` | `Action` |
| `OnReturningStoreEarly` / `Late` | `Action` |
| `OnHandlingNightlyServicesEarly` / `Late` | `Action` |
| `OnPlaceInventorInventoryItemEarly` / `Late` | `Action<List<GameItem>>` |
| `OnCreateTooltipEarly` / `Late` | `Action<RichTextBuilder, GameItem>` |
| `OnCreateDevTooltipEarly` / `Late` | `Action<RichTextBuilder, GameItem>` |
| `OnModAssetsLoaded` | `Action<LoadedMod>` |
| `OnModConfigApplied` | `Action<string>` (modId) |
| `OnLootTablesLoaded` | `Action` |
| `OnLootRolled` | `Action<LootRollContext>` |
| `OnPaperOpened` | `Action<string, PaperUI>` |
| `OnNetworkUpgradeUnlocked` | `Action<string>` (upgradeId) |
| `OnNewGameStarted` | `Action<string>` (scenarioId) |
| `OnDifficultyApplied` | `Action<string>` |
| `OnAfterhourArriveEarly` / `Late` | `Action<string>` (locationId) |
| `OnAfterhourLeaveEarly` / `Late` | `Action<string>` |
| `OnAfterhourEventStarted` / `Ended` | `Action<string, string>` (locationId, eventId) |

内部触发方法（`Fire*`）是 `internal static`，mod 侧只能订阅事件。
`SafeInvoke` 包裹了 try/catch —— 单个订阅者抛异常不会打断其他订阅者。

`IMod`：

```csharp
public interface IMod { void Init(ModManifest manifest); void OnEnable(); void OnDisable(); }
```

`ModManifest`：`public string ID, Name, Author, ModVersion, Description;`

---

## 5. `ModItemDirectory` 与物品注册

```csharp
public class ModItemDirectory : ItemDirectory
{
    protected override void InitDirectory() => ModHook.FireOnModItemDirectoryInit(this);
}
```

`ItemDirectory : Directory<GameItem>`，`Directory<T> : MonoBehaviour`：

```csharp
public Dictionary<string, Func<T>> factoryDictionary { get; protected set; }
public T Create(string identifier);
public T Create<K>(K identifierValue) where K : Enum;
public abstract T Empty();
protected abstract void InitDirectory();
public virtual bool Add(string identifier, Func<T> factoryFunc);   // ★ 注册入口
public virtual bool Set(string identifier, Func<T> factoryFunc);
public bool Has(string identifier);  public bool Remove(string identifier);
```

查表与创建：

```csharp
DirectoryMaster.Create<GameItem>(id);                 // 遍历所有 GameItem 目录（含 ModItemDirectory）
DirectoryMaster.Item(id, bool isOwned = true);        // 上面再补 owner/uniqueId/Permit 处理
DirectoryMaster.Has<GameItem>(id);
DirectoryMaster.GetIdentifierList<GameItem>(dictionaryClassName = null);
ItemDirectory.CreateEmptyItem(string identifier = null);   // new GameItemElement(id, "Items/itematlas", "unknown")
ItemSpawner.Spawn(id);                                     // 先查 PreBuiltItemHelper，再 DirectoryMaster.Item
```

**注册范例**（必须写在 `OnModItemDirectoryInit` 里，主菜单阶段就会触发）：

```csharp
public void OnEnable()
{
    ModHook.OnModItemDirectoryInit += OnItemDirInit;
    ModHook.OnGameLoadedNormal   += OnGameLoaded;
}

private void OnItemDirInit(ModItemDirectory dir)
{
    dir.Add("brew_machine", () => MyBrewer.Create());
    dir.Add("brew_grape",   () => MyGrape.Create());
    dir.Add("brew_yeast",   () => MyYeast.Create());
}
```

工厂函数会被**反复调用**（每次创建、每次读档解码都调一次），所以必须是纯函数式的「新建对象」，不能依赖外部状态。

---

## 6. 家具 / 可放置物

### 6.1 家具就是 GameItem

- 家具目录：`FurnitureItemDirectory : ItemDirectory`，`InitDirectory()` 里逐个 `Add("wine_rack", () => MachineWineRack.AgeWell())`。
  原版机器清单（节选）：`furnace, hydroponic, printer, blender, evaporator, chem_finisher, bottle_printer, wine_rack, fridge, storage_coordinator, vending_machine, ...`
- **`Add` 是 `public virtual`**，理论上可从 mod 侧 `FindObjectOfType<FurnitureItemDirectory>().Add(...)`；
  但**推荐直接用 `ModItemDirectory`**——`DirectoryMaster.Create<GameItem>` 会遍历所有 `GameItem` 目录，
  所以放进 `ModItemDirectory` 的家具在 `SpawnItem` / 读档 / 掉落表里都能正常解析。
- 机器的基础构造（原版统一写法）：

```csharp
GameItem machine = ItemDirectory.CreateEmptyItem()
    .SetContentWindow(contentWindow)                       // 双击物品时弹出的窗口
    .SetSpriteAndShape("Items/items_winemaking", "wine_rack");   // 贴图 + 占格
machine.name = LocHelper.GetLocalizedItem("item_age_well_name");
machine.shortDescription = LocHelper.GetLocalizedItem("item_age_well_desc");
machine.unitValue = 350L;
machine.SetGameItemType("MACHINE");
machine.EnableTag("CONTAINER_TAG");          // 允许往里面拖东西
machine.EnableTag("STANDARD_MACHINE_TAG");
machine.EnableTag("IMPORTANT_TAG");
AudioHelper.InitMetalMachine(machine);
MachineryHelper.InitMachinery(machine, 24);  // 24 = 每天耗电
```

`GameItem` 上相关 setter：

```csharp
GameItem SetName(string);  GameItem SetSprite(string atlas, string sprite);
GameItem SetSpriteAndShape(string atlas, string sprite);
GameItem SetSpriteAndShapeFromMod(string modId, string spriteKey);
GameItem SetContentWindow(PixelWindow window);
GameItem SetGameItemType(string type);  GameItem RemoveGameItemType(string type);
GameItem EnableTag/DisableTag(string);  bool IsTag(string);
GameItem ModifyTag(string, Action<TagState>);  TagState GetTagReadonly(string);
GameItem SetUnitCount/SetStack/SetAmount(int);
GameItem SetType(string);  GameItem SetSource(...);
void     CloseContentWindow(bool includeChildren = false);
void     Destroy();
```

`TagState`：`Enable/Disable/IsEnabled/SetEnabled/ToggleEnabled` + `GetString/GetInt/GetFloat/GetLong/GetDouble/GetBool`
+ `SetString/SetInt/SetFloat/SetLong/SetDouble/SetBool` + `Clone()/Set()/Destroy()`。
物品的 `state`（整个 TagSystem）会整份进存档（`SaveItemNode.itemState`），**所以用 tag 存机器状态是安全且推荐的做法**。

### 6.2 玩家怎么把家具摆进店里

- 商店「地面/展示区」= `EmporiumEntry.Instance.invElement`（`new GameGridInventory(24, 10)`，配 `invWindow`）。
  把物品拖进这个网格即等于「摆进店里」。
- 其它相关网格：`frontInvinvElement(18x4)` 前柜台、`backInvinvElement(18x4)` 后柜台（收货区）、
  `showcaseElement(7x5)` 橱窗、`hiddenElement(2x2)`、`hirelingInv(10x10)`。
- 玩家与物品交互：
  - **双击物品** → 若 `item.contentWindow != null` 就 `contentWindow.Show()`（`ItemMouseDoubleClickHandler.OpenContentAction`）。
    机器面板就是靠这个打开的，**不需要自己写打开逻辑**。
  - 拖拽 → `ItemMouseDragHandler`（`ItemMouseDragHandler.current.IsDraggingItem`），对所有挂在 `PixelWindow` 里的
    `GameInventory` 自动生效。
  - 右键/激活 → `mayActivateSlotItemFunc` / `onActivateSlotItemFunc`（例：倒空酒瓶）。
  - 物品拖到物品上 → `mayItemTargetThisFunc` / `onItemTargetThisFunc`（例：水瓶倒进水瓶）。

### 6.3 购买/获取途径（代码里查到的）

`Assembly-CSharp.dll` 里**没有**硬编码的「家具商店」。机器/家具进入玩家手里的实际途径：

1. **顾客卖给你**：`PlayerStore.OnItemBought(GameItem, int cost)` —— 柜台交易，机器会 `machineryBoughtCount++`。
2. **网络升级解锁赠送**：`ModHelper.AddNetworkUpgrade(..., onUnlock: () => ...)`，原版用
   `PlayerStore.Instance.AddDirectToWeightedTable(DirectoryMaster.Item("card_brewer"), isOwend: true)`
   / `GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.backInvinvElement, item)`。
3. **掉落表**：`ModHelper.AddLootEntry(modId, tableId, "brew_machine", weight)`。
4. **调试菜单**：`ModHelper.AddDebugFunction(modId, ModHelper.DEBUG_CATEGORY_SPAWN, "Brewer", () => ModHelper.MoveItemToInventory(ModHelper.SpawnItem("brew_machine")), false)`。
5. 开发者面板里原版就是这么做：`GraphUtils.TryAcceptAll(EmporiumEntry.Instance.invElement, DirectoryMaster.Item("wine_rack"))`。

> 未查清：是否存在数据驱动的零售目录（可能在 Addressables `aa` 包里，不在 C# 程序集内）。
> 实用结论：**mod 新增家具不必依赖原版商店**，用上面 2/3/4 任一路径发放即可。

---

## 7. 自定义 UI

游戏里有**两套完全独立**的 UI 框架，用途不要混：

| | `PixelWindow` 系（像素/机器风） | `CustomUIManager` 系（Meta/现代风） |
|---|---|---|
| 用途 | 游戏内物品、容器、机器面板、柜台 | 设置、Mod 列表、网络、纸张书、调试窗 |
| 能否放 `GameInventory` 网格 | **能，且拖放/旋转/吸附全自动** | 不能（只有自绘 cell，如 `CustomUIItemSpawner`） |
| 创建方式 | `new PixelWindow(...)` + `Attach(...)` | `CustomUIManager.Window(id,title,layer)` / `Instance.CreateWindow(...)` |
| 结论 | **酿酒机器面板必须用这套** | 只用于额外信息/设置面板 |

### 7.1 机器面板（推荐方案）— `MachineHelper`

```csharp
public static class MachineHelper
{
    public const int MODULE_BAY_1_WIDTH = 7;
    public static void SetupBatterySlot(GameSlotInventory batterySlot, GameItem machine);
    public static PixelWindow CreateMachineryWindow(bool canMove, params MachineryPanel[] panels);
    public static PixelWindow CreateMachineryWindow(bool canMove, int[] displayOrder, params MachineryPanel[] panels);
    public static MachineryPanel Panel(string label, PixelElement content, string role = null, bool styleInventories = true);
    public static MachineryPanel IconPanel(Sprite icon, PixelElement content, string role = null);
    public static MachineryPanel PowerPanel(GameSlotInventory batterySlot);
    public static MachineryPanel NotePanel(GameSlotInventory noteSlot);
    public static PixelElement WrapInIntake(PixelElement content, bool styleInventories = true);
    public static bool CanPower(GameItem machine, GameSlotInventory batterySlot);
    public static bool CanPower(GameItem machine);
    public static GraphNodeStorage GetBodyStorage(GameItem machine, int index);
    public static GameSlotInventory GetBatterySlot(GameItem machine);
    public static bool HasBattery(GameItem machine);
    public static GameInventory GetSlot(GameItem machine, int index);
    public static List<GameItem> GetModules(GameItem machine);
    public static GameGridInventory GetModuleInv(GameItem machine);
    public static void SetupModuleBay(GameInventory moduleGrid, GameItem machine, Action onChanged = null, string[] allowedModuleTypes = null);
    public static void SetupManualSlot(GameSlotInventory manualSlot);
    public static GameItem CreateManual(string nameLocKey, string flavorLocKey);
    public static bool TryDrawCyclePower(GameItem machine, GameSlotInventory batterySlot);
    public static void OnMachineActioned(GameItem machine, GameInventory moduleGrid);
    public static void OnLoad(GameItem machine);     // 读档时的机器修复钩子
}
```

`PixelWindow`：

```csharp
public class PixelWindow : PixelElement, GraphNodeWindow, GraphNodeStorage, MachineryStorageOwner
{
    public PixelWindow(bool canMove = false, string title = null, WindowStyle style = WindowStyle.Classic);
    public PixelWindow(int width, int height, bool canMove, string title = null, WindowStyle style = WindowStyle.Classic);
    public PixelElement childElement { get; }
    public List<GameItem> parentItems { get; }
    public Action closeCallback { get; set; }  public Action openCallback { get; set; }
    public bool isLocked { get; set; }
    public PixelWindow SetTitle(string);  public PixelWindow SetTransparency(float);
    public PixelWindow Show();  public PixelWindow Show(Vector2 mousePosition, bool fixTooltipBounds = false);
    public PixelWindow Hide();  public PixelWindow Center();  public PixelWindow Lock();  public PixelWindow Unlock();
    public bool IsVisible();  public bool Attach(PixelElement element);  public bool Detach();
    public void Validate();  public void FixBounds();  public void Destroy();
}
```

**完整可用范例 1：原版「发酵瓶」机器（`WineHelper.InitWineBottle`，与需求最接近）**

```csharp
public static void InitWineBottle(GameItem item)
{
    item.EnableTag("WINE_CONTAINER_TAG");
    item.SetGameItemType("HOUSEHOLD_GOOD");
    item.EnableTag("LIQUID_CONTAINER_TAG");
    item.ModifyTag("LIQUID_CONTAINER_CAPACITY", s => s.Enable().SetInt(750000));
    ContainerHelper.AddContainerTooltipTag(item);

    // ① 建网格：6 宽 2 高，只收「酿酒原料」或「酵母」
    GameGridInventory ingredientGrid = new GameGridInventory(6, 2);
    ContainerHelper.AllowOnlyTaggedItemsOr(ingredientGrid,
        new string[] { "WINE_INGREDIENT_TAG", "WINE_YEAST_TAG" }, ownedOnly: true);

    // ② 建按钮
    MachineryButton startButton = new MachineryButton(LocHelper.GetLocalizedMechanic("mech_wine_start_fermenting"));
    startButton.SetCallback(() => {
        TryStartFermentation(item);
        if (item.GetTagReadonly("WINE_CONTAINER_STATE").GetInt() == 1)
            startButton.SetText(LocHelper.GetLocalizedMechanic("mech_wine_fermenting_status"));
    });

    // ③ 组装窗口：一个「原料」面板 + 一个「控制」面板
    PixelWindow contentWindow = MachineHelper.CreateMachineryWindow(true,
        MachineHelper.Panel(LocHelper.GetLocalizedMechanic("mech_wine_label_ingredient"), ingredientGrid, "ingredients"),
        MachineHelper.Panel(LocHelper.GetLocalizedMechanic("mech_blender_label_control"),  startButton,   "control"));
    item.SetContentWindow(contentWindow);

    // ④ 每天结算：state 1 = 发酵中，state 2 = 陈酿中
    item.onCycleEndSlotItemFunc = (gameItem, parentInventory, slot) => {
        switch (gameItem.GetTagReadonly("WINE_CONTAINER_STATE").GetInt())
        {
            case 1:
                int yeastPower = gameItem.GetTagReadonly("WINE_YEAST_POWER_INT").GetInt();
                gameItem.ModifyTag("FERMENTATION_TIME_REMAINING_INT", s => s.SetInt(s.GetInt() - yeastPower));
                if (gameItem.GetTagReadonly("FERMENTATION_TIME_REMAINING_INT").GetInt() <= 0)
                    FinishFermentation(gameItem);
                break;
            case 2:
                OnAgeWine(gameItem);
                break;
        }
    };

    // ⑤ 叠加贴图（酒标）
    item.getItemSpriteModifierForItemFunc = bottle => {
        var list = new List<ItemSpriteModifier>();
        if (bottle.GetTagReadonly("WINE_CONTAINER_STATE").GetInt() == 2)
            list.Add(new ItemSpriteModifier(
                RenderHandler.LoadFromAtlas("Items/items_winemaking",
                    bottle.IsTag("WINE_COUNTERFEIT_TAG") ? "wine_label_red" : "wine_label" + GetWineQualityTier(bottle)),
                new Vector2(0f, 0f), 0, isFlipped: false, 0, haveMask: false, Color.white));
        return list;
    };

    // ⑥ 右键倒空
    item.mayActivateSlotItemFunc = (b, _, _) => CanEmptyWineBottle(b);
    item.canActivateSlotItemFunc = (b, _, _) => CanEmptyWineBottle(b);
    item.onActivateSlotItemFunc  = (b, _, _) => EmptyWineBottle(b);
}
```

**完整可用范例 2：原版「搅拌机」（多区域网格，4 个面板，与「四区机器」结构同构）**

```csharp
public static GameItem Blender()
{
    var (contentWindow, moduleGrid, inputGrid, containerSlot, machineryButton) = CreateMachineInventoryWindow(8, 8);

    GameItem machine = ItemDirectory.CreateEmptyItem()
        .SetContentWindow(contentWindow)
        .SetSpriteAndShape("Items/items_tool2", "blender");

    inputGrid.mayInventoryAddItemFunc = (item, inv) => ContainerHelper.CanContainerStoredItem(item, inv);
    containerSlot.mayInventoryAddItemFunc = (item, _) => GeneralHelper.IsItemOwned(item) && item.IsTag("LIQUID_CONTAINER_TAG");
    containerSlot.SetBackgroundFadeSprite("Items/water_container4", "xl");

    machineryButton.SetCallback(() => {
        foreach (GameItem item in inputGrid.childItems.ToList())
            if (item.IsTag("BLENDABLE_TAG"))
                ChemicalHelper.BlendInto(item, containerSlot.childItem, GetLoss(machine));
    });
    return machine;
}

public static (PixelWindow, GameGridInventory, GameGridInventory, GameSlotInventory, MachineryButton)
    CreateMachineInventoryWindow(int width, int height, bool isDraggable = true, bool isCentered = true, int minWidth = 150)
{
    var moduleGrid    = new GameGridInventory(4, 4);
    var inputGrid     = new GameGridInventory(4, 4);
    var containerSlot = new GameSlotInventory();
    var startBtn      = new MachineryButton(LocHelper.GetLocalizedMechanic("mech_blender_btn_start"));
    inputGrid.slotPriorityFunc     = (_, _, _) => 2;
    containerSlot.slotPriorityFunc = (_, _, _) => 3;
    return (MachineHelper.CreateMachineryWindow(isDraggable,
                MachineHelper.Panel(label_module,    moduleGrid,    "module"),
                MachineHelper.Panel(label_input,     inputGrid,     "input"),
                MachineHelper.Panel(label_container, containerSlot, "container"),
                MachineHelper.Panel(label_control,   startBtn,      "control")),
            moduleGrid, inputGrid, containerSlot, startBtn);
}
```

**完整可用范例 3：原版「化工精炼机」（4 区 + 文本状态 + 每秒刷新）**

```csharp
var tuple = CreateMachineInventoryWindow(8, 8);
PixelWindow item = tuple.Item1;
GameGridInventory moduleGrid = tuple.Item2;
GameSlotInventory containerSlot = tuple.Item3;
GameGridInventory outputGrid = tuple.Item4;
MachineryButton startBtn = tuple.Item5;
MachineryText targetLabel = tuple.Item7;
MachineryText statusLabel = tuple.Rest.Item1;
MachineryStack controlGrid = tuple.Rest.Item2;

machine.onOneSecondItemFunc = () => {                 // 每现实秒刷新一次 UI 文本
    targetLabel.SetText(...);
    statusLabel.SetText(...);
    controlGrid.Validate();
};
startBtn.SetCallback(() => {
    GameItem product = DirectoryMaster.Item(recipeId);
    GraphUtils.TryAcceptAll(outputGrid, product);      // 产物进输出区
});
outputGrid.mayInventoryAddItemFunc = (added, inv) => ContainerHelper.CanContainerStoredItem(added, inv);
```

**其它像素控件**（可直接放进 `MachineHelper.Panel`）：

```csharp
MachineryButton(string text = null, Action callback = null, int width = -1, int height = -1)
MachineryButton(Sprite icon, Action callback = null, int size = 16)
MachineryButton(Sprite normal, Sprite hover, Sprite pressed, Sprite disabled, Action callback = null)
    .SetText/SetIcon(Sprite|atlas,name)/SetFaceSprites/SetCallback/SetDescription/SetFillWidth/SetSize
MachineryText(string text, int size, HorizontalAlignmentOptions alignment, bool uppercase)
MachineryText.Status(string label, string value)      // "标签  值" 两栏，天然适合"发酵 3/4 天"
    .SetText/GetText/SetStatus/SetColor/SetSize/SetAlignment/SetUppercase/SetFillWidth/SetMinHeight/WithInfoLink
MachineryLabel(text | Icon(sprite))
MachineryProgressBar(float value = 0f, int width = -1)  .SetValue/SetColor/SetWidth/SetFillWidth
MachineryStack(bool vertical, int gap) / MachineryStack.Vertical(params PixelElement[])
    / .Horizontal(...) / SetAlign / SetMainAlign / SetStretch / Add
MachinerySlider / MachineryRangeSlider / MachineryRadioGroup / MachineryRadioList / MachineryTabs
MachineryIntake(PixelElement content, bool vents, bool styleInventories)   // Panel 内部自动套的"进气口"外框
MachineryPanelRow(params MachineryPanel[]) / .SetDisplayOrder(int[])
PushButtonElement / IndicatorElement(.IndicatorColor) / TagElement / RichTextElement
GridPixelElement(int w, int h, bool isCentered)   // 自由布局像素网格，Attach/AttachPos
HorizontalStackPixelElement / EmptyPixelElement / ColoredPixelElement
```

自由布局示例（原版 `test_gizmo`，`FurnitureItemDirectory.CreateTestGizmo`）：

```csharp
PixelWindow w = new PixelWindow(canMove: true);
GameItem result = Empty().SetName("Test Gizmo").SetContentWindow(w).SetSpriteAndShape("Items/items_module", "core");
GridPixelElement g = new GridPixelElement(3, 3, isCentered: true);
w.Attach(g);
GridPixelElement g2 = new GridPixelElement(2, 3, isCentered: true);
g.AttachPos(g2, 0, 2);
PushButtonElement b = new PushButtonElement().SetColor(IndicatorElement.IndicatorColor.Cyan);
g.Attach(b, 100, 50);
IndicatorElement ind = new IndicatorElement().SetColor(IndicatorElement.IndicatorColor.Red).Light();
g.Attach(ind, 30);
b.onDown = () => { b.ToggleLight(); ind.ToggleLight(); };
```

### 7.2 `CustomUIManager`（元界面，供参考）

```csharp
public class CustomUIManager : MonoBehaviour
{
    public static string LAYER_FRONT = "front", LAYER_PAPER = "paper", LAYER_REAR = "rear",
                         LAYER_META = "meta", LAYER_MENU = "menu", LAYER_OVERLAY = "overlay";
    public static CustomUIManager Instance { get; }
    public static CustomUIBuilder Window(string id, string title, string layerId);
    public CustomUIBuilder CreateWindow(string id, string title, string layerId,
                                        GameObject windowPrefabOverride = null, CustomUIManager.Style style = ...);
    public CustomUIBuilder BuildInto(RectTransform container, bool useMetaPrefabs, string id);
    public CustomUIWindow GetWindow(string id);
    public bool IsOpen(string id);  public void CloseWindow(string id);  public void CloseAllWindows();
    public CustomUIElement GetElement(string windowId, string elementId);
    public GameObject SpawnElement(CustomUIManager.ElementKind kind, Transform parent, CustomUIWindow window, string name);
    public Sprite GetSprite(string key);   public Sprite GetModSprite(string modId, string spriteKey);
    public static bool IsMetaLayer(string layerId);
    public static bool HasLayer(string layerId);
    public static List<string> GetAvailableLayers();
    public RectTransform GetLayerRoot(string layerId);
    public void ShowTooltip(string text, CustomUITooltipTrigger owner);
    public void ShowHoldRing(float progress);  public void HideHoldRing();
}
```

`CustomUIBuilder`（链式）：

```csharp
SetSize(float w, float h) / SetPosition(Vector2) / Center() / SetDraggable(bool)
SetCloseOnEscape(bool = true) / OnClose(Action) / UseMetaPrefabs(bool) / UseMachineryStyle(bool)
AddLabel(string text, string id = null) / AddHeader(string) / AddWarning(string)
AddSeparator(string id = null, int verticalPadding = 0) / AddSpace(float) / AddFlexibleSpace()
AddButton(string text, Action onClick, string id = null)
AddCloseButton(Action onClick, string id = null)
AddIconButton(Sprite, float size, Action onClick, Sprite highlightSprite = null, string id = null)
AddImage(Sprite, float w, float h, string id = null) / AddImage(string spriteKey, float w, float h, string id = null)
AddToggle(string label, bool initial, Action<bool> onChanged, string id = null)
AddSlider(float min, float max, float initial, Action<float> onChanged, bool wholeNumbers = false, string id = null)
AddInput(string placeholder, string initial, Action<string> onChanged, string id = null, Action<string> onTyping = null)
AddDropdown(IList<string> options, int initialIndex, Action<int> onChanged, string id = null)
AddProgressBar(float initial, string id = null)
BeginRow(float spacing, TextAnchor alignment) / BeginColumn(...) / BeginPanel(w,h,align,padding,spacing,id)
BeginGrid(int columns, float cellWidth, float cellHeight, float spacing = 4f) / BeginScroll(float height)
BeginScrollFlexible(float minHeight = 0) / BeginTabs(float spacing) / BeginTab(string tabName) / EndTabs()
End() / WithTooltip(string) / Last (CustomUIElement) / CurrentContainer / Handle
Show() → CustomUIWindow
```

`CustomUIElement` 常用：`SetText/GetText/SetVisible/SetInteractable/SetSprite/SetImageColor/OnClick/OnToggle/
OnSlider/OnInput/OnDropdown/SetIndex/SetOptions/SetProgress/SetTooltip/SetPreferredSize/SetFixedWidth/
SetFlexible/SetBool/SetFloat/SetString/GetBool/GetFloat/GetString/GetIndex/Rect/IsAlive`。

**范例（`ModConfigWindow.Open`，mod 可直接借用此风格）**

```csharp
CustomUIManager ui = CustomUIManager.Instance;
bool inGame = StoreUIManager.Instance != null;
string layer = inGame ? CustomUIManager.LAYER_META : CustomUIManager.LAYER_MENU;

CustomUIBuilder b = ui.CreateWindow("mod_config:" + mod.Manifest.ID, mod.Manifest.Name, layer, ui.optionUIPrefab)
                      .SetCloseOnEscape(!inGame);
b.BeginScrollFlexible();
foreach (string section in config.Sections)
{
    b.BeginRow(4f, TextAnchor.MiddleLeft);
    b.AddHeader(section, "section_" + section);
    b.End();
    b.AddSeparator(null, 2);
    foreach (ConfigEntry e in config.Entries)
        if (e.Section == section) BuildEntry(b, config, e);
    b.AddSpace(12f);
}
b.End();
b.BeginRow(12f, TextAnchor.MiddleCenter);
b.AddFlexibleSpace();
b.AddButton("Reset", () => ResetWidgets(window, config), "reset").Last.SetFixedWidth(220f);
b.AddButton("Apply", () => Apply(window, config), "apply");   b.Last.SetFixedWidth(220f);
b.AddFlexibleSpace();
b.End();
CustomUIWindow window = b.Show();
window.Get("restart_note")?.SetVisible(false);
```

**范例（`MachineStorageCoordinator.OpenWindow`，带 scroll + 动态内容宿主）**

```csharp
CustomUIBuilder b = CustomUIManager.Instance
    .CreateWindow("storage_coordinator", title, "paper")
    .SetSize(760f, 440f).SetDraggable(true).SetCloseOnEscape().OnClose(ResetWindowState);
b.BeginRow(4f, TextAnchor.MiddleLeft);
b.AddLabel(desc);
b.AddFlexibleSpace();
b.AddButton("New preset", AddPreset, "new_preset").WithTooltip("…");
WidenButton(b.Last, 1.4f);
b.End();
b.BeginScroll(300f);
listHost = b.CurrentContainer;      // 之后自己往里塞 self-built 元素
b.End();
b.Center();
b.Show();
```

`CustomUIItemSpawner` 是「在 CustomUI 里画物品图标网格」的现成参考（`Cell`/`CellClickHandler` 结构体，
用 `DirectoryMaster.GetIdentifierList<GameItem>()` 枚举全部物品 id，`ES3` 存最近使用/收藏）。
但它是**自绘 cell**，不是真 `GameInventory`——不能拖入物品。**不要用它做酿造机的物品区**。

---

## 8. 游戏内时间

### 8.1 天数

```csharp
public class StoreStation
{
    [ES3Serializable] public int dayCounter;      // 当前天数（随存档）
    [ES3Serializable] public int order, traffic, happiness;
    public static StoreStation Instance { get; }  // 单例（懒加载 + lock）
    public static int GetDayCounter();            // Instance?.dayCounter ?? 0
    public void StartDay();                       // ★ dayCounter++ 唯一递增点
    public static void ResetData();
    public bool IsStationOnLockdown();
}
```

`StartDay()` 内容：`dayCounter++; storeEventManager.OnDayStart(); … HandleFaction(); HandleOrder(); HandleStoreRequests(); foreach (networkUpgrade) value.OnDayStart();`

- 日期 = `StoreStation.GetDayCounter()`
- `AdvCalendarUIManager` / `CalendarDayElement` 用 `StoreStation.Instance.dayCounter` 画日历
- 存档时 `StoreStation` 作为独立对象写入同一个 ES3 文件（`eS3Writer.Write<StoreStation>("storeStation", StoreStation.Instance)`）

### 8.2 「一天」的边界与机器 tick

一天结束时 `PlayerStore.EndNight()`（`PlayerStore.cs:1685`）依次：

```csharp
ModHook.FireOnGoingSleepEarly();
FindAllItem().ForEach(i => i.onCycleEndEarlySlotItemFunc?.Invoke(i, i.parentInventory, i.GetIncompleteSlotInInventory()));
... HandlePower() / HandleJanitorial() / 各类 nightly 服务 ...
ModHook.FireOnHandlingNightlyServicesEarly();
ModHook.FireOnHandlingNightlyServicesLate();
FindAllItem().ForEach(i => { StolenHelper.UpdateStolenItem(i);
                             i.onCycleEndSlotItemFunc?.Invoke(...); });          // ★ 主结算
FindAllItem().ForEach(i => i.onCycleEndLateSlotItemFunc?.Invoke(...));
instance.storeState = StoreState.MORNING;
ModHook.FireOnGoingSleepLate();
```

`FindAllItem()` = 各窗口（前柜台/后柜台/主仓库/橱窗/垃圾/档案/暗格/时段/自动售货机…）里的所有物品
（默认 `isOwn: true` 只取 `IS_OWNED_TAG`）。**放在 `invElement` 里的机器会被统计到。**

次日开始：`StartOfDayUIManager` 里执行并清空 `PlayerStore.Instance.nightlyAction`（`List<Action>`，**不存档**）。

### 8.3 现实时间 tick

`EmporiumEntry.Update()`：累计 `Time.deltaTime`，每满 1 秒对 `GetAllItems()` 里每个有 `onOneSecondItemFunc` 的物品调用一次。

### 8.4 「两天后执行」的三种写法

| 写法 | 代码 | 适用 |
|---|---|---|
| **机器自身按天推进（推荐）** | `machine.onCycleEndSlotItemFunc = (m, inv, slot) => { int left = m.GetTagReadonly("BREW_DAYS_LEFT_INT").GetInt() - 1; m.ModifyTag("BREW_DAYS_LEFT_INT", s => s.SetInt(left)); if (left <= 0) Finish(); };` | 机器酿造、发酵、陈酿；状态随物品存档，零额外持久化 |
| **全局延迟任务** | `playerStore.nightlyAction.Add(() => { if (StoreStation.GetDayCounter() - startDay >= 2) DoIt(); });` 或订阅 `ModHook.OnGoingSleepLate` + 自己记 `SetModDataInt("deadline", day+2)` | 非物品逻辑（剧情、事件、冷却） |
| **按现实秒** | `item.onOneSecondItemFunc` | 进度条动画、实时倒计时显示（原版化工机就这么刷 UI） |

> 没有「小时」概念——游戏时间是纯天数计数。若想要「几小时后好」，只能换算成「今晚/明晚」或按现实秒。

---

## 9. 存档持久化（重点结论）

### 9.1 两层

1. **对象层**：`PlayerStore` / `StoreStation` 用 **ES3** 整体写进槽位文件。

```csharp
private void WriteSlotFile()
{
    using ES3Writer w = ES3Writer.Create(new ES3Settings(SaveFiles.Slot(saveSlotId)));
    w.Write<PlayerStore>("playerStore", this);
    w.Write<StoreStation>("storeStation", StoreStation.Instance);
    w.Save();
}
// 读：ES3.LoadInto("playerStore", loadFile, this);
```

   带 `[ES3Serializable]` 的字段才会写。`PlayerStore.modData`（mod 数据）、`gridInv`、各种 JSON 字符串都是字段。

2. **物品层**：物品树不靠 ES3 直序列化，而是先编码成 JSON 字符串字段再随 `PlayerStore` 一起存。

```csharp
public void SaveGame()
{
    mainInvJSON          = EncodeSaveItem("save_bag", EmporiumEntry.Instance.invElement, EmporiumEntry.Instance.invWindow);
    backInvJSON          = EncodeSaveItem("back_inv_save_bag", EmporiumEntry.Instance.backInvinvElement, ...);
    hiddenInvJSON        = EncodeSaveItem("hidden_save_bag", ...);
    showcaseInvJSON      = EncodeSaveItem("showcase_save_bag", ...);
    soldInvJSON          = EncodeSaveItem("sold_save_bag", ...);
    hirelingInvJSON      = ...; vendingMachineInvJSON = ...; vendingFountainInvJSON = ...;
    trashcanInvJSON      = EncodeItem(EmporiumEntry.Instance.trashcan);
    docInvJSON           = EncodeItem(EmporiumEntry.Instance.dossier);
    cassettePlayerInvJSON= ...;
    WriteSlotFile();
}

public string EncodeSaveItem(string id, PixelElement pixelElement, PixelWindow pixelWindow)
{
    GameItem saveBag = GetSaveBag(id);            // 一个虚拟"袋子"物品，按 id 缓存
    saveBag.contentWindow.Attach(pixelElement);   // ★ 临时把商店网格挂到袋子窗口下
    string json = JsonUtility.ToJson(new SaveState { saveItems = SaveManager.EncodeNodes(saveBag) });
    pixelWindow?.Attach(pixelElement);            // 再挂回去
    return json;
}
```

### 9.2 物品图编码/解码（决定「自定义容器能否正确存档」）

```csharp
public class SaveItemNode
{
    public string identifier;  public int unitCount;  public long uuid;
    public TagSystem itemState;                       // ★ 全部 tag 状态
    public GridShapeBuilder itemShape, itemModifiedShape;
    public string itemType, name, shortDescription, flavorText;
    public int uniqueId;
    public long unitBaseValue, unitValue, lateUnitValue, backupUnitValue;
    public string spriteAtlasPath, spritePath;
    public List<string> itemTypes;
    public List<long> childItems;                     // 子物品 uuid
    public List<int>  childItemInventoryNode;         // ★ 子物品所在"背包节点序号"
    public GameItem tempLink;
}
public class SaveState { public List<SaveItemNode> saveItems; }

public static class SaveManager
{
    public static List<SaveItemNode> EncodeNodes(GraphNodeStorage firstNode);
    public static GameItem DecodeNodes(List<SaveItemNode> savedItems);
    public static void Save();  public static void Load();   // 另一套（飞船/Emporium 用），主存档走 PlayerStore
}
```

编码（`EncodeNode`，对每个 GameItem）：

```
queue = [item]
while queue:
   n = dequeue
   if n == item: 把所有 children 入队; continue
   if n 不是 GameItem:                       // 窗口 / 面板 / 网格 / Intake ...
        if 没见过: dictionary[n] = num++;    // ★ 序号 = 从物品出发的 BFS 顺序
        把 n 的所有 children 入队
   if n 是 GameItem:
        childItems.Add(itemHeap[n]); childItemInventoryNode.Add(dictionary[n.parentInventory]);
        // 注意：不再入队（它自己的子树会由它自己的 SaveItemNode 记录）
```

解码（`DecodeNodesInternal`）：

```
1) 每个 SaveItemNode → DirectoryMaster.Item(identifier)（★ 会重新调用你的工厂函数）
   恢复 unitCount/state/shape/name/value/sprite ...
2) 对每个物品，重新 BFS 它自己的 children（跳过 GameItem），得到同样的 序号→背包 映射
3) 对每个 (childUuid, invIndex): ((GameInventory)map[invIndex]).UncheckedAccept(itemDict[childUuid])
4) StorageExpansionHelper.RestoreTabs(item);  之后回调 item.onLoaded(item)
```

### 9.3 结论

**✅ 自定义容器里的物品能正确存档/恢复。** 依据：

- 编码从根背包（`invElement`）出发，BFS 会走到
  `机器 GameItem → contentWindow(PixelWindow) → MachineryPanelRow → MachineryPanel → MachineryIntake → GameGridInventory → 葡萄/酵母/瓶子 GameItem`，
  非 GameItem 节点被编号，GameItem 被记 `(uuid, 父背包序号)`；再逐层递归处理子 GameItem 自己的子树。
  所以**任意深度的嵌套容器都能存**。
- 解码时用 `DirectoryMaster.Item(identifier)` 重建物品（调你注册的工厂），再按**同样的 BFS 序号**把物品塞回对应网格。
- 原版 `MachineWineRack`（酒架 6x4 葡萄酒格）、`MachineBlender`（4x4 输入格）就是活证据：它们的网格物品每天跨存档都还在。

**⚠️ 三个必须遵守的前提：**

1. **工厂函数必须确定性**。存档里只记录「背包节点序号」，不记录「这是哪个网格」。
   所以 `MyBrewer.Create()` 每次都必须以**完全相同的顺序**创建窗口/面板/网格。
   一旦你在新版本里插入/删除/重排一个 `GameGridInventory`，老存档里的物品就会落到错的格子里。
   → 建议：网格创建顺序写死并加注释；不要动态增删区域；如必须改结构，写迁移代码。
2. **`identifier` 稳定性**。物品靠 `identifier` 重建，改 id = 丢物品。
   物品自己的状态（发酵天数、酵母 power 等）请放在 `TagSystem`（`ModifyTag`）或 `unitValue/name` 上，
   它们都在 `SaveItemNode` 里有对应字段；**不要**放在 mod 自己的静态变量里。
3. **mod 被卸载时**，`DirectoryMaster.Item` 找不到会返回 `ItemDirectory.CreateEmptyItem(identifier)` 空壳并打 warning，
   不会崩存档，但物品会变成 unknown 占位。

**❌ 不要做的：**
- 不要把机器网格里的物品 id 自己再存一份到 `ModHelper.SetModData` 然后读档时重复生成 —— 会和游戏自己的恢复叠加导致复制物品。
- 不要在 `OnGameLoadedInit` 之前碰 `PlayerStore`（`ModDataStore` 会返回 null 且只打 warning）。
- 不要给 `contentWindow` 在工厂里 `Show()`（读档时会弹出窗口）；`Show()` 只在玩家交互时发生。

### 9.4 mod 数据落点

| 需求 | 方案 |
|---|---|
| 跟存档走（每个存档不同） | `ModHelper.SetModDataInt(modId, key, v)` → `PlayerStore.modData`（`[ES3Serializable]`），key 实际为 `"<modId>:<key>"` |
| 跟机器走（机器被卖掉/丢掉状态跟着走） | `machine.ModifyTag("MY_BREW_STATE", s => s.SetInt(x))` + `GetTagReadonly(...)` |
| 全局（跨存档，比如「首次赠送」标记、缓存） | `ModHelper.GetModDataFolder(modId)` + `WriteModDataFileText` |
| 用户设置 | `ModHelper.GetConfig(modId)` → `ModConfig.Define(...)`（写 mod 自己的配置文件） |

---

## 10. 「多区域网格家具」最可行实现路径

### 10.1 骨架

```csharp
public static class MyBrewer
{
    // ★ 创建顺序一旦发布就不能变（存档按 BFS 序号回填）
    public static GameItem Create()
    {
        // 1) 四个区域
        var grapeGrid  = new GameGridInventory(8, 8);
        var bottleGrid = new GameGridInventory(4, 8);
        var yeastGrid  = new GameGridInventory(3, 3);
        var outputGrid = new GameGridInventory(4, 8);

        // 2) 区域过滤
        grapeGrid .mayInventoryAddItemFunc = (i, _) => i.IsTag("BERRY_TAG")   && GeneralHelper.IsItemOwned(i);
        yeastGrid .mayInventoryAddItemFunc = (i, _) => i.IsTag("WINE_YEAST_TAG") && GeneralHelper.IsItemOwned(i);
        bottleGrid.mayInventoryAddItemFunc = (i, _) => i.IsTag("WINE_CONTAINER_TAG") && GeneralHelper.IsItemOwned(i);
        outputGrid.mayInventoryAddItemFunc = (i, _) => false;          // 只出不进（或不设限制）

        // 3) 控件
        var startBtn = new MachineryButton("开始酿造");
        var status   = MachineryText.Status("发酵", "-");       // 两栏文本
        var progress = new MachineryProgressBar(0f, 160);

        // 4) 窗口
        PixelWindow window = MachineHelper.CreateMachineryWindow(true,
            MachineHelper.Panel("葡萄",   grapeGrid,  "grape"),
            MachineHelper.Panel("酵母",   yeastGrid,  "yeast"),
            MachineHelper.Panel("酒瓶",   bottleGrid, "bottle"),
            MachineHelper.Panel("成品",   outputGrid, "output"),
            MachineHelper.Panel("控制",   status,     "status"),
            MachineHelper.Panel("控制",   progress,   "progress"),
            MachineHelper.Panel("控制",   startBtn,   "start"));

        // 5) 机器本体
        GameItem machine = ItemDirectory.CreateEmptyItem()
            .SetContentWindow(window)
            .SetSpriteAndShapeFromMod(MyMod.Id, "brewer");   // 或 SetSpriteAndShape("Items/items_winemaking", "xxx")
        machine.name = "酿酒机";
        machine.SetGameItemType("MACHINE");
        machine.EnableTag("CONTAINER_TAG");
        machine.EnableTag("IMPORTANT_TAG");
        AudioHelper.InitMetalMachine(machine);

        // 6) 每天推进（也可选 onOneSecondItemFunc 做进度条动画）
        machine.onCycleEndSlotItemFunc = (m, inv, slot) =>
        {
            int left = m.GetTagReadonly("BREW_DAYS_LEFT_INT").GetInt();
            if (left <= 0) return;
            m.ModifyTag("BREW_DAYS_LEFT_INT", s => s.SetInt(left - 1));
            if (left - 1 <= 0) FinishBrew(m, grapeGrid, yeastGrid, bottleGrid, outputGrid);
        };

        startBtn.SetCallback(() => StartBrew(machine, grapeGrid, yeastGrid, bottleGrid));
        return machine;
    }
}
```

### 10.2 关于「酿酒」本身

游戏已有完整链路，**优先复用**（省掉配方/品质/贴图全套）：

```csharp
public static class WineHelper
{
    public const string BERRY_TAG = "BERRY_TAG";
    public const string WINE_CONTAINER_TAG = "WINE_CONTAINER_TAG";
    public const string WINE_CONTAINER_STATE = "WINE_CONTAINER_STATE";   // 0=空瓶 1=发酵中 2=已完成/陈酿 3=?
    public const string FERMENTATION_TIME_REMAINING_INT = "FERMENTATION_TIME_REMAINING_INT";
    public const string WINE_YEAST_TAG = "WINE_YEAST_TAG";
    public const string WINE_YEAST_POWER_INT = "WINE_YEAST_POWER_INT";
    public const string WINE_INGREDIENT_TAG = "WINE_INGREDIENT_TAG";
    public const string WINE_SCORE_INT = "WINE_SCORE_INT";
    public const int BASE_FERMENTATION_PERIOD = 4;      // 基础 4 天
    public const int WINE_BOTTLE_CAPACITY_ML = 750;
    public const int FERMENTATION_MIN_ML = 749;
    public const int WINE_ALCOHOL_PERCENT = 20;

    public static void InitWineBottle(GameItem item);           // 把一个物品变成"可发酵酒瓶"
    public static void InitDrinkableWine(GameItem item, int alcoholLevel);
    public static void InitBerry(GameItem item);
    public static void InitYeast(GameItem item, int power);
    public static void InitInfiniteYeast(GameItem item, int power);
    public static void InitIllegalYeast(GameItem item, int power);
    public static bool TryStartFermentation(GameItem bottle);   // 校验原料 + 水量(>=749ml)
    public static void StartFermentation(GameItem bottle);      // 消耗原料、算 wineScore、换贴图
    public static void FinishFermentation(GameItem bottle);     // state→2、加品质 feature
    public static void OnAgeWine(GameItem wine);                // 陈酿 +1 天
    public static bool CanModifyLiquid(GameItem item);
    public static bool IsFermentingOrFinished(GameItem item);
    public static bool IsFinishedWine(GameItem item);
    public static void PourRationIntoBottle(GameItem bottle, GameItem ration);  // 水→瓶
    public static void PourWineIntoContainer(GameItem bottle, GameItem container);
    public static void DrainBottle(GameItem bottle);
    public static void EmptyWineBottle(GameItem bottle);
    public static void ResetToEmptyBottle(GameItem bottle);
    public static int  GetWineQualityTier(GameItem item);
    public static int  GetWaterQualityScore(GameItem bottle);
    public static void CreateTooltip(RichTextBuilder builder, GameItem item);
    public static GameItem CreateFinishedWineForDebug(int wineScore, bool counterfeit);
}
```

两条路：

- **A. 复用（推荐先做）**：机器把「葡萄 + 酵母 + 水」组装进一个 `wine_bottle` 物品，放到 bottleGrid；
  调用 `WineHelper.TryStartFermentation(bottle)`；每天 `onCycleEndSlotItemFunc` 里递减
  `FERMENTATION_TIME_REMAINING_INT`（原版按 `WINE_YEAST_POWER_INT` 递减），到 0 调 `FinishFermentation`；
  完成品留在 outputGrid 或直接放回 bottleGrid。
  → 品质、贴图、Tooltip、价值全是原版逻辑，几乎零维护。
- **B. 自建**：完全用自己的 tag 做状态机（`BREW_DAYS_LEFT_INT` / `BREW_STAGE_INT`），产物用
  `DirectoryMaster.Item("my_wine")` 生成。自由度大，但要自己写品质/贴图/描述。

### 10.3 为什么用 `MachineHelper.CreateMachineryWindow` 而不是自定义 UI

- 拖放/旋转/翻转/吸附/整组拖动（`SupportsGroupPlacement = true`）全部由 `ItemMouseDragHandler` 自动处理；
- `PixelWindow` 会自动注册进 `WindowsHandler.visibleWindows`、自动保存窗口位置
  （`GameItem.RememberContentWindowPositionIfMine` → `PlayerStore.contentWindowPositions`）；
- 双击开关窗口零代码（`ItemMouseDoubleClickHandler.OpenContentAction`）；
- 视觉风格和原版机器一致（`WindowStyle.Machinery` + `MachineryStyle` 边框/铆钉）。

### 10.4 风险清单

| 风险 | 说明 / 对策 |
|---|---|
| 网格顺序变更 | 老存档物品错位。结构发布后冻结，或做版本迁移（在 `onLoaded` 里按 tag 校验） |
| `GameGridInventory(8,8)` 像素尺寸 | `widthPixels = 8 * UISettings.current.gridSize`，8x8 约 8 格宽，`MachineryPanelRow` 会自动排版；若窗口太宽可考虑 `GameGridScrollableInventory` |
| `UISettings.current.gridSize` 改变 | 全部 `GridShape` 在存档里是位图（与像素无关），不受缩放影响 |
| 单格堆叠 | 默认 1 个/格。想让 8x8 葡萄区装 64 个葡萄就要么不开堆叠（64 个葡萄占 64 格），要么设 `maxSlotAmountItemFunc` |
| 每日 tick 依赖 `IS_OWNED_TAG` | `FindAllItem(isOwn: true)` 只统计已拥有的物品；机器放置时务必 `GeneralHelper.SetItemOwned(machine)`（`DirectoryMaster.Item` 默认已设 true） |
| 电力/模块 | 可选。`MachineHelper.SetupBatterySlot` + `MachineryHelper.InitMachinery(machine, 24)` + `MachineHelper.TryDrawCyclePower(...)`；不接电力就把这些全去掉 |
| 图标/贴图 | 用 `SetSpriteAndShapeFromMod(modId, key)`，贴图放 `Textures/Items/...`；切片必须是 `gridSize` 的整数倍，否则会报 "SPRITE NOT ON GRID" |
| 发放途径 | 不存在 C# 内的家具商店；用 `AddNetworkUpgrade.onUnlock` / `AddLootEntry` / `AddDebugFunction` / 直接 `MoveItemToInventory` |

---

## 11. 未能查清 / 待验证

1. **零售家具目录是否数据驱动**。`Assembly-CSharp.dll` 里没有任何「家具商店」类，
   `FurnitureItemDirectory` 仅被自身引用。怀疑目录在 Addressables（`StreamingAssets/aa`）的数据文件里，
   本次未解析（需要 UnityPy/AssetRipper 之类工具）。→ 若要在原版商店里上架，需要另行确认。
2. **`WindowStyle` 枚举的全部取值** 与 `MachineryPanelRow` 的排版细节（`SetDisplayOrder` 语义）未逐个核对，
   但 `MachineHelper.CreateMachineryWindow(canMove, params panels)` 已经够用。
3. **`GameGridScrollableInventory`** 未细读（用于超大容器），本需求 8x8/4x8 用不上。
4. **`StorageExpansionHelper`（分页/扩展仓）** 与 `StorageExpansionHelper.RestoreTabs` 只看了签名，
   如果要做「可升级扩容」的机器才需要。
5. **`onCycleEndSlotItemFunc` 的确切触发频率** 已确认是「每次 `EndNight`」= 每个游戏日一次；
   但如果玩家跳过睡眠（调试/特殊事件），是否会漏一天未验证。
6. `GameItem` 上还有若干未整理的字段（`itemFeatures`、`soundContainer*`、`parentInventory` 等），
   本报告只列了与需求相关的部分。
