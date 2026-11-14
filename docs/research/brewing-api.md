# Probably Stolen — 酿酒（Wine / 发酵）系统 API 调研

> 只读调研。未修改游戏目录任何文件，也未改动 `Modding/MyMod`。
> 目标程序集：`Probably Stolen_Data/Managed/Assembly-CSharp.dll`（Unity 2021.3.45f2 / Mono）

---

## 0. 调研方法与工具链备注（给后续 agent 省时间）

- `ilspycmd` 能装上，但 `ilspycmd.exe` 被本机 **Device Guard / 应用控制策略** 拦截（`0x800711C7`）。
  自己编译的任何辅助 exe/dll 同样被拦（`dotnet xxx.dll` 直接 FileLoadException），所以**不要**走「写个 .NET 小工具」这条路。
- 最终方案：**Python + `dnfile` + `dncil`** 直接读元数据和 IL（本机已装，无需管理员权限）。
  脚本在 `C:\Users\ciul4\AppData\Local\Temp\psd_tools\psd.py`（不在游戏目录），支持：
  - `types <regex>` 列类型全名
  - `sig <regex>` 列字段/属性/方法签名（含泛型、数组、嵌套类型）
  - `il <类型全名> [方法名regex]` 反汇编 IL（token 已被解析成 类型::成员 + 完整签名 + 字符串字面量）
  - `consts <regex>` 读 `const` 常量真实值（很多关键数值是 const，IL 里看不到）
  - `xr <regex>` 全程序集交叉引用（按解析后的成员名/字符串匹配）
- 本文所有「反编译片段」都是**从 IL 还原的等价 C#**（不是原始源码），语义等价，变量名是我起的。
- 本地化中文名是从 `Probably Stolen_Data/resources.assets` 里按「key 表顺序 ↔ value 表顺序 + 索引标记」对齐出来的，可信度高（见 §7）。

---

## 1. 结论速览（TL;DR）

- 整个酿酒系统几乎是**一个静态类** `WineHelper`（约 33 个方法、23 个 const），
  没有 `Bottle` / `Wine` / `Fermenter` 之类专用类。
- 「啤酒瓶」= 普通 `GameItem`，identifier 是 **`wine_bottle`**，靠一组 **字符串 tag** 表示状态：
  `WINE_CONTAINER_TAG` / `WINE_CONTAINER_STATE` / `WINE_SCORE_INT` / `FERMENTATION_TIME_REMAINING_INT` /
  `WINE_YEAST_POWER_INT` / `WINE_COUNTERFEIT_TAG`。
- 果子 = `GameItem`，identifier `wine_berry`，分值存在 **`BERRY_CONDITION_INT`**（每颗单独存 int，不是查表）。
- 水不是物品，是**容器上的液体 tag**（`liquid_water` 那套 `Liquid` 系统），品质由「纯度 0..10000」算出来。
- 「发酵」= `WineHelper.StartFermentation(GameItem)`；2 天 = 剩余量 4，每晚（`EndNight`）减酵母 power(2)。
- **臻品窖藏 = 最终 wineScore 恰好等于 5**（不是 ≥3、也不是 ≥6；≥6 会变成高一档的「琼浆玉液」）。

---

## 2. 相关类型清单

| 类型 | 作用 |
|---|---|
| `WineHelper` | **系统核心**，静态类，全部规则都在这里 |
| `WineDirectory : ItemDirectory` | 注册 6 个物品：`wine_yeast` / `wine_superyeast` / `wine_yeast_red` / `wine_yeast_infinite` / `wine_bottle` / `wine_berry` |
| `MachineWineRack` | 「陈化酒架」机器（`item_age_well_name`），是**写机器 mod 的最佳参考** |
| `ItemFeatureList` | `CreateWineQualityFeature(int wineScore)` 生成品质 Feature |
| `ItemConditionList` | 六个酒质 Condition（含中文名/加值） |
| `AgableHelper` | 陈化（AGE_INT 每天 +1，unitValue 每天 +mod） |
| `WaterHelper` | `AddWater` / `GetWaterPurity` / `GetCurrentCapacityML` / `DrainBottle` |
| `WaterFeatureHelper` | 水质档位（`WATER_PURITIES` / `WATER_QUALITY` 显示名） |
| `Liquid` | 液体定义表（`water` / `alcohol` / `wine_extract` / `chemical_contaminant` …） |
| `GameItem` / `TagSystem` / `TagState` | 物品与状态容器 |
| `PlantList.HarvestBloomberry` / `RollBloomberryCondition` | 蓝莓采收时决定 `BERRY_CONDITION_INT` |
| `InsBerryUI` | 浆果检视 UI（显示 quality / variant） |
| `PlayerStore.EndNight` | 每天结算的驱动点（会调用每个物品的 `onCycleEndSlotItemFunc`） |
| `MachineHelper` / `MachineryHelper` / `ContainerHelper` | 机器窗口、槽位、容器限制 |

`WineDirectory` 里还有一个 `WineAirlock()`（`Fermentation Airlock`，发酵栓），它**只是配方材料**，
没有逻辑——`wine_bottle` 物品是直接可以发酵的。

---

## 3. 数据模型：一切都是 GameItem + tag

```csharp
// GameItem 关键成员（节选）
class GameItem : GameItemFunc {
    string identifier;                     // 物品 id，如 "wine_bottle" / "wine_berry"
    string name, shortDescription, flavorText;
    int unitCount; long unitValue;
    TagSystem state;                       // 真实状态
    TagSystem modifiedState;               // 形态/校验用副本
    List<ItemFeature> itemFeatures;        // 价值/品质修正
    List<string> itemTypes;                // "ALCOHOL" / "SUBSTANCE" / "LUXURY_ITEM" / "HOUSEHOLD_GOOD" ...
    PixelWindow contentWindow;             // 容器窗口（内含 6x2 网格背包）

    bool        IsTag(string tagName);
    TagState    GetTagReadonly(string tagName);   // 不存在会自动创建（默认 int 0 / disabled）
    GameItem    ModifyTag(string tagName, Action<TagState> action, bool skipValidate = false);
    bool        EnableTag(string tagName, bool skipValidate = false);
    bool        DisableTag(string tagName, bool skipValidate = false);
    GameItem    SetSpriteAndShape(string atlasPath, string spritePath);
    GameItem    SetContentWindow(PixelWindow window);
    void        CloseContentWindow(bool includeChildren);
    void        Validate();
    void        Destroy();
    GameItem    TryAcceptAll(GameItem item);      // 把 item 塞进自己的内容窗口（走图遍历）
    void        SetGameItemType(string t);  void RemoveGameItemType(string t);
    void        AddItemFeature(ItemFeature f); void RemoveFeatureByCategory(string c);
}

class TagState {
    string identifier; bool valueEnabled; string internalValueString;
    int valueInt; float valueFloat; long valueLong; double valueDouble; bool valueBool;
    bool      IsEnabled();  TagState SetEnabled(bool);  TagState Enable();  TagState Disable();
    string    GetString();  int GetInt();   TagState SetInt(int val);      // 其余 Set*/Get* 同理
}
```

> `GetTagReadonly()` 返回的是 `Clone()`，**只读**；任何写入必须走 `ModifyTag(tag, s => s.SetInt(x), false)`。

---

## 4. `WineHelper` 全量常量（都是 `const`，从 Constant 表读出）

```csharp
const int    WINE_BOTTLE_CAPACITY_ML  = 750;
const int    FERMENTATION_MIN_ML      = 749;   // 起酵所需最少水量（≈满瓶）
const int    BASE_FERMENTATION_PERIOD = 4;     // 发酵总工作量（不是天数）
const int    WINE_ALCOHOL_PERCENT     = 20;
const int    WINE_EXTRACT_PER_MILLE_MAX = 20;
const int    WINE_COUNTERFEIT_EXTRACT_PER_MILLE = 8;
const int    WINE_COUNTERFEIT_CONTAMINANT_PER_MILLE = 8;
const int    WINE_AGE_VALUE_MOD_PER_DAY = 3;
const string WINE_SPRITE_ATLAS = "Items/items_winemaking";

const string WINE_CONTAINER_TAG                 = "WINE_CONTAINER_TAG";      // 是酒瓶
const string WINE_CONTAINER_STATE               = "WINE_CONTAINER_STATE";    // 0=空 1=发酵中 2=已成品
const string WINE_SCORE_INT                     = "WINE_SCORE_INT";          // 酿酒总分
const string FERMENTATION_TIME_REMAINING_INT    = "FERMENTATION_TIME_REMAINING_INT"; // 剩余工作量
const string WINE_YEAST_POWER_INT               = "WINE_YEAST_POWER_INT";    // 本瓶酵母 power（每晚扣减量）
const string WINE_COUNTERFEIT_TAG               = "WINE_COUNTERFEIT_TAG";    // 假酒（用了非法酵母）
const string WINE_INGREDIENT_TAG                = "WINE_INGREDIENT_TAG";     // 是"可酿酒果子"
const string WINE_YEAST_TAG                     = "WINE_YEAST_TAG";
const string WINE_YEAST_REUSABLE_TAG            = "WINE_YEAST_REUSABLE_TAG"; // 酵母 bioreactor 不消耗
const string WINE_YEAST_ILLEGAL_TAG             = "WINE_YEAST_ILLEGAL_TAG";  // 非法酵母 → 假酒
const string BERRY_TAG                          = "BERRY_TAG";
const string BERRY_CONDITION_INT                = "BERRY_CONDITION_INT";     // ★每颗果子的"酿酒分值"
const string BERRY_VISUAL_VARIANT_INT           = "BERRY_VISUAL_VARIANT_INT";// 0..2 外观（正常/小/…）

// ★ 水质 → 加分，索引 = 纯度档位
static readonly int[] WATER_QUALITY_SCORE = { 1, 1, 0, -1, -2, -3 };
```

其余相关常量：

```csharp
// WaterFeatureHelper
static readonly int[] WATER_PURITIES = { 9990, 9900, 9800, 9600, 9000, 3500 };
const int PURE=0, HIGH_QUALITY=1, BASE=2, GHOST=3, RUST=4, GUTTERFLOW=5;
const int PURE_WATER_PURITY=9990, HIGH_QUALITY_WATER_PURITY=9900, BASE_WATER_PURITY=9800,
          GHOST_WATER_PURITY=9600, RUST_WATER_PURITY=9000, GUTTERFLOW_WATER_PURITY=3500;

// AgableHelper
const string AGE_INT = "AGE_INT";
const string AGE_VALUE_MOD_PER_DAY_INT = "AGE_VALUE_MOD_PER_DAY_INT";
const int DEFAULT_AGE_VALUE_MOD_PER_DAY = 3;

// MachineWineRack（陈化酒架）
const int AGE_WELL_BASE_DAYS = 1;
const int AGE_WELL_QUALITY_PER_EXTRA_DAY = 125;
```

---

## 5. `WineHelper` 方法签名全表

```csharp
static int  GetWineQualityTier(GameItem item);            // 0..5
static int  GetWaterQualityScore(GameItem bottle);        // -3..+1
static int  GetWineExtractPerMille(GameItem bottle);
static int  GetWineContaminantPerMille(GameItem bottle);

static void PourWineIntoContainer(GameItem bottle, GameItem container); // 750ml 倒出
static bool CanModifyLiquid(GameItem item);               // state == 0
static bool IsFermentingOrFinished(GameItem item);        // state >= 1
static bool IsFinishedWine(GameItem item);                // state == 2
static bool IsWaterRation(GameItem item);                 // id == water_ration / water_ration_small
static int  RationVolumeMl(GameItem ration);              // 750 / 375
static void PourRationIntoBottle(GameItem bottle, GameItem ration);
static void DrainBottle(GameItem bottle);
static bool CanBottleSourceTarget(GameItem s, GameItem t);
static bool CanFillBottle(GameItem source, GameItem bottle);
static void SetupBottleLiquidTransfer(GameItem item);     // 注册拖拽倒水交互

static void InitWineBottle(GameItem item);                // ★ 把普通 item 变成酒瓶（含 6x2 UI）
static void InitDrinkableWine(GameItem item, int alcoholLevel);
static bool CanEmptyWineBottle(GameItem item);
static void EmptyWineBottle(GameItem item);
static void ResetToEmptyBottle(GameItem item);            // ★ 喝完/倒空复位

static bool TryStartFermentation(GameItem item);          // ★ 带 UI 校验与弹窗，返回是否成功
static void StartFermentation(GameItem item);             // ★ 真正起酵（无校验、无 UI）
static void FinishFermentation(GameItem item);            // ★ 结算成品
static void FinishCounterfeitWine(GameItem item);
static GameItem CreateFinishedWineForDebug(int wineScore, bool counterfeit); // ★ 一步造酒
static void OnAgeWine(GameItem item);                     // 陈化 tick（假酒不陈化）

static void InitBerry(GameItem item);                                  // 随机 condition
static void SetBerryCondition(GameItem item, int condition, int visualVariant);
static void InitYeast(GameItem item, int power);
static void InitInfiniteYeast(GameItem item, int power);  // + REUSABLE
static void InitIllegalYeast(GameItem item, int power);   // + ILLEGAL

static void CreateTooltip(RichTextBuilder builder, GameItem item);
```

---

## 6. 关键逻辑（IL 还原）

### 6.1 酒瓶初始化 `InitWineBottle` —— 既是容器也是机器 UI

```csharp
static void InitWineBottle(GameItem item) {
    item.EnableTag(WINE_CONTAINER_TAG, false);
    AgableHelper.InitAgable(item, 3);                       // 每天 +3 价值
    item.SetGameItemType("HOUSEHOLD_GOOD");
    item.EnableTag("LIQUID_CONTAINER_TAG", false);
    item.ModifyTag("LIQUID_CONTAINER_CAPACITY", s => { s.Enable(); s.SetInt(750000); }, false); // 750.000 ml（单位 1/1000 ml）
    foreach (var l in Liquid.Liquids)
        item.ModifyTag(l.currentPart, s => { s.Enable(); s.SetInt(0); }, false);   // 清零所有液体 tag
    ChemicalHelper.InitContainerTemp(item);
    ContainerHelper.AddContainerTooltipTag(item);
    SetupBottleLiquidTransfer(item);

    var inv = new GameGridInventory(6, 2);                  // 12 格内容背包
    ContainerHelper.AllowOnlyTaggedItemsOr(inv, new[]{ WINE_INGREDIENT_TAG, WINE_YEAST_TAG }, true);

    var startButton = new MachineryButton(Loc("mech_wine_start_fermenting"), null, -1, -1);
    startButton.SetCallback(() => TryStartFermentation(item));

    var window = MachineHelper.CreateMachineryWindow(false, new[] {
        MachineHelper.Panel(Loc("mech_wine_label_ingredient"), inv,          "ingredients", true),
        MachineHelper.Panel(Loc("mech_blender_label_control"),    startButton,"control",     true),
    });

    item.onCycleEndSlotItemFunc      = /* 每天结算，见 6.2 */;
    item.getItemSpriteModifierForItemFunc = /* 按 WINE_CONTAINER_STATE 换图 */;
    item.mayActivateSlotItemFunc / canActivateSlotItemFunc / onActivateSlotItemFunc = /* 倒空 */;
    item.ModifyTag(WINE_CONTAINER_STATE, s => { s.Enable(); s.SetInt(0); }, false);
    item.onLoaded = /* 读档后重挂 UI/交互 */;
    item.SetContentWindow(window);
}
```

> `TryAcceptAll` 的可见性说明：`GameItem.TryAcceptAll(x)` → `GraphUtils.TryAcceptAll` →
> `GameItem.TryFindOneValidInventorySlot`，而 `GameItem` 的实现是**递归遍历自己的 children**
> （`contentWindow` 在 `SetContentWindow` 里被 `AddParent`，窗口又指向 `GameGridInventory`），
> 所以 `bottle.TryAcceptAll(berry)` 能直接把果子塞进 6x2 网格。
> 但网格被 `AllowOnlyTaggedItemsOr(..., ownedOnly: true)` 限制：**物品必须是 owned**（
> `DirectoryMaster.Item(id, isOwned: true)` 或 `ModHelper.SetItemOwnerShip(item, true)`）。
>
> 注意 `InitWineBottle` 是**幂等可重复调用**的（`ResetToEmptyBottle` 结尾也会再调它）。
> mod 想「造一个空酒瓶」最省事的写法：`DirectoryMaster.Item("wine_bottle", true)`（这条工厂链内部就是
> `SetSpriteAndShape("Items/items_winemaking","wine_bottle") → InitWineBottle → AudioHelper.InitGlass`）。

### 6.2 每天推进（2 天是怎么来的）★

```csharp
// 注册点（InitWineBottle 内）
item.onCycleEndSlotItemFunc = (GameItem it, GameInventory _, SlotMarker _) => {
    int state = it.GetTagReadonly(WINE_CONTAINER_STATE).GetInt();
    if (state == 0) return;
    if (state == 1) {
        int power = it.GetTagReadonly(WINE_YEAST_POWER_INT).GetInt();
        it.ModifyTag(FERMENTATION_TIME_REMAINING_INT, s => s.SetInt(s.GetInt() - power), false);
        if (it.GetTagReadonly(FERMENTATION_TIME_REMAINING_INT).GetInt() <= 0)
            WineHelper.FinishFermentation(it);
        return;
    }
    if (state == 2) WineHelper.OnAgeWine(it);     // 成品在瓶里也会每天陈化
};
```

```csharp
// 唯一的触发源：PlayerStore.EndNight()
FindAllItem(true).ForEach(i => {
    StolenHelper.UpdateStolenItem(i);
    i.onCycleEndSlotItemFunc?.Invoke(i, i.parentInventory, i.GetIncompleteSlotInInventory());
});
```

结论：
- **「一天」= 一次 `EndNight`**（关店过夜结算），不是 Unity 协程、不是计时器。
- 起酵时 `FERMENTATION_TIME_REMAINING_INT = BASE_FERMENTATION_PERIOD = 4`。
- 标准酵母 `wine_yeast` power = **2** → 4 -2 -2 = 0 → **第 2 个夜晚结束成酒**（即玩家说的「2 天」）。
  `wine_superyeast` power = 4 → **1 天**；`wine_yeast_red`（非法）power = 2；`wine_yeast_infinite` power = 2。
- 剩余天数在 UI 上显示为 `ceil(remaining / power)`：`(remaining + power - 1) / power`。

### 6.3 起酵 `StartFermentation` ★★（酿酒分值的唯一计算处）

```csharp
static void StartFermentation(GameItem item) {
    if (item.GetTagReadonly(WINE_CONTAINER_STATE).GetInt() != 0) return;   // 已经不是空瓶就退出

    item.ModifyTag(FERMENTATION_TIME_REMAINING_INT, s => { s.Enable(); s.SetInt(4); }, false);
    item.ModifyTag(WINE_CONTAINER_STATE,             s =>            s.SetInt(1),         false);

    int wineScore   = item.GetTagReadonly(WINE_SCORE_INT).GetInt();  // 新瓶 = 0
    bool hasYeast   = false;
    int  berryCount = 0;

    foreach (var child in GraphUtils.FindAllChildrenType<GameItem>(item, null, null)) {
        if (child.IsTag(WINE_INGREDIENT_TAG)) {
            int cond = child.GetTagReadonly(BERRY_CONDITION_INT).GetInt();
            if (cond < 0) cond += 1;          // ★ 负数只补 1（-2→-1, -1→0）
            wineScore += cond;
            berryCount++;
        }
        if (child.IsTag(WINE_YEAST_TAG)) {
            int yeastPower = child.GetTagReadonly(WINE_YEAST_POWER_INT).GetInt();
            item.ModifyTag(WINE_YEAST_POWER_INT, s => { s.Enable(); s.SetInt(yeastPower); }, false);
            hasYeast = true;
            if (child.IsTag(WINE_YEAST_ILLEGAL_TAG))
                item.EnableTag(WINE_COUNTERFEIT_TAG, false);          // 假酒标记
            if (child.IsTag(WINE_YEAST_REUSABLE_TAG)) {               // 培养罐：不消耗
                child.parentInventory?.Expel(child);
                GraphUtils.EmporiumTryAdd(child);
            } else {
                child.Destroy();                                      // 普通酵母被吃掉
            }
        }
    }

    if (berryCount >= 2) wineScore += 1;        // ★ 两颗及以上果子 +1
    if (hasYeast)        wineScore += 1;        // ★ 有酵母 +1
    wineScore += GetWaterQualityScore(item);    // ★ 水质 ±

    item.ModifyTag(WINE_SCORE_INT, s => { s.Enable(); s.SetInt(wineScore); }, false);
    if (!item.IsTag(WINE_YEAST_POWER_INT))       // 没放酵母 → power 兜底 1（不会卡死）
        item.ModifyTag(WINE_YEAST_POWER_INT, s => { s.Enable(); s.SetInt(1); }, false);

    DrainBottle(item);                            // ★ 把水全排干（此时水质已被记住，只剩 wineScore）
    item.DisableTag("LIQUID_CONTAINER_TAG", false);
    ContainerHelper.RemoveContainerTooltipTag(item);
    item.RemoveGameItemType("HOUSEHOLD_GOOD");
    item.SetSpriteAndShape("Items/items_winemaking", "wine_fermenting");
    item.Validate();
    item.CloseContentWindow(true);                // 果子/酵母已处理，关窗
    item.SetContentWindow(null);
}
```

要点：
- 只有 `WINE_INGREDIENT_TAG` 的子物品计入分值，**不区分品种**（目前只有 `wine_berry`）。
- 分值**不封顶也不保底**，可以为 0 / 负数。
- `StartFermentation` 本身**不做任何前置校验**（校验都在 `TryStartFermentation`，见 §7.3），
  所以 mod 可以放心直接调它。
- ⚠️ `DrainBottle` 会把水排空，但分值已经算进 `WINE_SCORE_INT`，之后无法再改水质。

### 6.4 结算 `FinishFermentation` ★★

```csharp
static void FinishFermentation(GameItem item) {
    item.ModifyTag(WINE_CONTAINER_STATE, s => s.SetInt(2), false);
    item.unitValue = 100;

    if (item.IsTag(WINE_COUNTERFEIT_TAG)) { FinishCounterfeitWine(item); return; }

    item.name             = LocItem("item_wine_bloomberry_name");    // ★ 名字固定，与品质无关
    item.shortDescription = LocItem("item_wine_bloomberry_desc");

    int score = item.GetTagReadonly(WINE_SCORE_INT).GetInt();
    item.AddItemFeature(ItemFeatureList.CreateWineQualityFeature(score));   // ★ 品质 Feature

    int tier = GetWineQualityTier(item);
    if (tier > 0) {
        InitDrinkableWine(item, 4 + tier);           // 酒精强度 = 4 + tier，并挂"喝掉"回调
        item.SetGameItemType("SUBSTANCE");
        item.SetGameItemType("ALCOHOL");
        if (tier >= 4) item.SetGameItemType("LUXURY_ITEM");
    }
    item.SetSpriteAndShape("Items/items_winemaking", "bottle" + tier);      // bottle0..bottle5
    item.Validate();
}
```

假酒分支：

```csharp
static void FinishCounterfeitWine(GameItem item) {
    item.name = LocItem("item_wine_gloomberry_name");
    item.shortDescription = LocItem("item_wine_gloomberry_desc");
    ContrabandHelper.InitContrabandItem(item, 2);
    item.EnableTag("ENDANGERMENT_ITEM_TAG", false);
    item.unitValue = 44;
    item.ModifyTag(WINE_SCORE_INT, s => { s.Enable(); s.SetInt(3); }, false);   // ★ 假酒分值强制 3
    InitDrinkableWine(item, 6);
    item.SetGameItemType("SUBSTANCE"); item.SetGameItemType("ALCOHOL");
    item.SetSpriteAndShape("Items/items_winemaking", "bottle2");
    item.Validate();
}
```

### 6.5 档位换算（两处同一套阈值，必须一致）

```csharp
static int GetWineQualityTier(GameItem item) {
    int s = item.GetTagReadonly(WINE_SCORE_INT).GetInt();
    if (s >= 6) return 5;
    if (s == 5) return 4;
    if (s >= 3) return 3;
    if (s == 2) return 2;
    if (s == 1) return 1;
    return 0;
}
```

```csharp
// ItemFeatureList
static ItemFeature CreateWineQualityFeature(int wineScore) {
    ItemCondition c;
    if      (wineScore >= 6) c = ItemConditionList.CreateMasterVintage();   // 琼浆玉液
    else if (wineScore == 5) c = ItemConditionList.CreateReserve();         // 臻品窖藏
    else if (wineScore >= 3) c = ItemConditionList.CreateProductionWine();  // 量产酒液
    else if (wineScore == 2) c = ItemConditionList.CreateHomebrew();        // 手工自酿
    else if (wineScore == 1) c = ItemConditionList.CreateBucketBrew();      // 粗桶劣酿
    else                     c = ItemConditionList.CreateUndrinkable();     // 难以下咽

    var f = new ItemFeature("CATEGORY_WINE_QUALITY");
    f.identifier = "CATEGORY_WINE_QUALITY";
    f.isFeatureMatch = true;
    f.useCondition = true;
    f.SetConditions(c, c);      // fake = real
    return f;
}
```

### 6.6 水质

```csharp
static int GetWaterQualityScore(GameItem bottle) {
    int purity = WaterHelper.GetWaterPurity(bottle);            // 0..10000
    int idx = WaterFeatureHelper.GetPurityArrayIndex(purity);   // 0..5
    idx = Mathf.Clamp(idx, 0, WATER_QUALITY_SCORE.Length - 1);
    return WATER_QUALITY_SCORE[idx];
}

// WaterFeatureHelper
static int GetPurityArrayIndex(int purity) {
    for (int i = 0; i < WATER_PURITIES.Length; i++)   // {9990,9900,9800,9600,9000,3500}
        if (purity >= WATER_PURITIES[i]) return i;
    return 5;
}

// WaterHelper.GetWaterPurity：水的纯度 = 水的量 × 10000 / 容器内液体总量
static int GetWaterPurity(GameItem c) {
    if (!c.IsTag("LIQUID_CONTAINER_TAG")) return 0;
    int water   = c.GetTagReadonly(Liquid.GetCurrentPartFromId("water")).GetInt();
    int total   = 0;  foreach (var l in Liquid.Liquids) total += c.GetTagReadonly(l.currentPart).GetInt();
    return total > 0 ? (int)((long)water * 10000 / total) : 0;
}
```

`WaterHelper.AddWater(container, grade, addAmount, useRange, low, high, initWaterFeature)` 的 **grade → 纯度区间**：

| grade | 纯度区间 | 档位 | 显示名 key | wineScore 加分 |
|---|---|---|---|---|
| 0 | 9990–9999 | 纯净 PURE | `mech_condition_pure_water` | **+1** |
| 1 | 9900–9989 | 优质 HIGH_QUALITY | `mech_condition_high_quality_water` | **+1** |
| 2 | 9800–9899 | 基础 BASE | `mech_condition_basewater` | 0 |
| 3 | 9600–9799 | 幽灵 GHOST | `mech_condition_ghostwater` | −1 |
| 4 | 9000–9599 | 锈水 RUST | — | −2 |
| ≥5 | 3500–8999 | 沟渠水 GUTTERFLOW | — | −3 |

- 水的单位是 **1/1000 ml**（`GETCurrentCapacityML = Σ(liquid tag) / 1000`，瓶容量 750000 = 750 ml）。
- 具体纯度是在区间内**随机滚**出来的（`RNG.GetRandomInt(low, high)` 或 `RollNearLowerEdge`），
  但区间跨不过档位阈值，所以**加分是确定的**（grade 1 永远 +1，grade 2 永远 0）。
- 常见灌水路径：`wine_bottle` 里放 `water_ration`（750ml）→ `WineHelper.PourRationIntoBottle(bottle, ration)`
  → 内部就是 `AddWater(bottle, grade:2, 750*1000, useRange:false, 0,0, initWaterFeature:true)`，
  **拿到的永远是"基础水"(9800–9899) → 加分为 0**。
- 想要「优质水上」必须自己调 `AddWater(bottle, 1, 750000, false, 0, 0, true)`（或 grade 0 = 纯净）。
- 纯度在 `StartFermentation → DrainBottle` 之后就没了（水被排干），只剩 `WINE_SCORE_INT`。

### 6.7 果子（蓝莓）分值

```csharp
// 物品工厂 WineDirectory.WineBerry()
item.SetSpriteAndShape("Items/items_winemaking", "wine_berry");
item.name = LocItem("item_wine_berry_name");  item.unitValue = 14;
WineHelper.InitBerry(item);                    // 随机初值（仅"野生/直接生成"时用）
item.SetGameItemType("FOOD");
FoodItemHelper.InitFoodItem(item, 150, false, false);
AudioHelper.InitProduce(item); InspectableHelper.InitInspectableItem(item);
PlantGeneticsHelper.ApplyFactoryGenetics(item, "bloomberry_seed", null);

static void InitBerry(GameItem item) {
    item.EnableTag(BERRY_TAG, false);
    int condition = RNG.GetRandomInt(-2, 1);      // -2,-1,0,1
    if (condition >= 0) condition += 1;           // 0→1, 1→2
    item.ModifyTag(BERRY_CONDITION_INT,      s => { s.Enable(); s.SetInt(condition); }, false);
    item.ModifyTag(BERRY_VISUAL_VARIANT_INT, s => { s.Enable(); s.SetInt(RNG.GetRandomInt(0,2)); }, false);
    item.EnableTag(WINE_INGREDIENT_TAG, false);
}
```

**真正种出来的果子**由植株健康度决定（`PlantList.HarvestBloomberry`）：

```csharp
static int RollBloomberryCondition(int health) {          // health = PlantHarvestContext.health
    int roll = RNG.GetRandomInt(0, 99);
    if (health <= 25) return roll < 55 ? -2 : -1;
    if (health <= 55) return roll < 25 ? -2 : (roll < 70 ? -1 : 1);
    if (health <= 85) return roll < 25 ? -1 : (roll < 70 ?  1 : 2);
    /* >85 */         return roll < 40 ?  1 : 2;
    // visualVariant: condition == -1 时固定 0，否则随机 0..2（HarvestBloomberry 内实现）
}
```

- 所以 `BERRY_CONDITION_INT` 的**存储取值集合 = {−2, −1, 1, 2}**（永远不是 0），**每颗单独存**，不查表。
- 进 `StartFermentation` 后会做 `cond<0 ? +1`，**实际参与计算的集合 = {−1, 0, 1, 2}**。
- 玩家口中的「正常果 2 分 / 小果 1 分」= condition 2 / 1；−1、0 就是劣质/次品。
- 外观 `BERRY_VISUAL_VARIANT_INT` 只是贴图，**不影响分值**。

### 6.8 陈化（酒架）

```csharp
// AgableHelper
static void InitAgable(GameItem item, int valueModPerDay) => item.ModifyTag("AGE_VALUE_MOD_PER_DAY_INT", s => { s.Enable(); s.SetInt(valueModPerDay); }, false);
static bool HasAged(GameItem item) => item.IsTag("AGE_INT");
static void OnAge(GameItem item) {
    item.ModifyTag("AGE_INT", s => { s.Enable(); s.SetInt(s.GetInt() + 1); }, false);
    int mod = item.IsTag("AGE_VALUE_MOD_PER_DAY_INT") ? item.GetTagReadonly("AGE_VALUE_MOD_PER_DAY_INT").GetInt() : 3;
    item.unitValue += mod;
}
// WineHelper.OnAgeWine：假酒直接 return，其余转 AgableHelper.OnAge
```

`MachineWineRack.AgeWell()`（`item_age_well_name`）：
- 机器本体 `MACHINE` + `CONTAINER_TAG` + `STANDARD_MACHINE_TAG`，`MachineryHelper.InitMachinery(machine, 24, false)`（耗电 24）
- 1 个电池槽 + 1 个模块槽 + 1 个酒的网格槽
- `machine.onCycleEndSlotItemFunc` → 对槽内每瓶调用 `WineHelper.OnAgeWine`
- `GetAgingDays(machine) = 1 + Math.Max(0, MachineryHelper.GetCurrentQualityBonus(machine)) / 125`
- 陈化**不改变品质档位/名称**，只加 `AGE_INT` 与 `unitValue`。

---

## 7. 「臻品窖藏」的判定条件（逐条）

### 7.1 名字对应关系（从 resources.assets 对齐出来的）

| key（ItemCondition.identifier） | ItemCondition.display（loc key） | 中文 | modValue | tier |
|---|---|---|---|---|
| `wine_quality_master_vintage` | `mech_condition_wine_master_vintage` | **琼浆玉液** | +75 | 5 |
| `wine_quality_reserve` | `mech_condition_wine_reserve` | **臻品窖藏** | +50 | 4 |
| `wine_quality_production` | `mech_condition_wine_production` | 量产酒液 | +25 | 3 |
| `wine_quality_homebrew` | `mech_condition_wine_homebrew` | 手工自酿 | 0 | 2 |
| `wine_quality_bucket_brew` | `mech_condition_wine_bucket_brew` | 粗桶劣酿 | −15 | 1 |
| `wine_quality_undrinkable` | `mech_condition_wine_undrinkable` | 难以下咽 | −100 | 0 |

> 判定依据：key 表顺序与 value 表顺序 + 值表里的索引标记（0..5）完全一致，
> 且 modValue 单调递减与中文名语气一致（琼浆玉液 > 臻品窖藏 > 量产 > 自酿 > 粗桶 > 难咽）。

### 7.2 全部条件（必须同时满足）

1. `WINE_CONTAINER_STATE == 2`（`FinishFermentation` 已执行）
2. **没有** `WINE_COUNTERFEIT_TAG`（即没放 `wine_yeast_red` 非法酵母；否则一律走假酒分支，分值被强制为 3）
3. `WINE_SCORE_INT == 5` —— **恰好 5**：
   - `≥6` → tier 5 琼浆玉液（更高档）
   - `3..4` → tier 3 量产酒液
   - 因此**「≥3」是错的**，5 是一个夹在中间的单点窗口。
4. 最终 `ItemFeature(category="CATEGORY_WINE_QUALITY")` 的 condition = `wine_quality_reserve`
5. 附带效果（复现时需一致）：
   - `tier = 4` → `InitDrinkableWine(item, 4 + 4 = 8)`，`ALCOHOL_VALUE = 8`
   - itemTypes += `SUBSTANCE`、`ALCOHOL`，且 `tier>=4` → 再加 `LUXURY_ITEM`
   - 贴图 `Items/items_winemaking` + `bottle4`
   - `unitValue` 基础 100，再叠加 condition 的 modValue（+50%）
   - 物品名 = `item_wine_bloomberry_name`（**所有成品酒同名**，品质只体现在 condition 行和图标）

### 7.3 `wineScore` 展开公式

```
wineScore = Σ clamp(berryCondition_i)      // clamp: -2→-1, -1→0, 其余原样 ∈{1,2}
          + (berryCount >= 2 ? 1 : 0)
          + (hasYeast ? 1 : 0)
          + WATER_QUALITY_SCORE[waterPurityIndex]   // +1 / +1 / 0 / -1 / -2 / -3
```

于是「2 颗果子 + 1 酵母 + 水」时：

| 果子分值 | 水和档位 | wineScore | 结果 |
|---|---|---|---|
| 1 + 1 | 优质(1) / 纯净(0) | 2+1+1+1 = **5** | **臻品窖藏** ✅ |
| 1 + 1 | 基础(2) | 2+1+1+0 = 4 | 量产酒液 |
| 2 + 1 | 基础(2) | 3+1+1+0 = **5** | **臻品窖藏** ✅ |
| 2 + 1 | 优质(1) | 3+1+1+1 = 6 | 琼浆玉液 |
| 2 + 2 | 优质(1) | 4+1+1+1 = 7 | 琼浆玉液 |
| 2 + 2 | 幽灵(3) | 4+1+1−1 = 5 | **臻品窖藏** ✅ |

> ⚠️ **与任务描述不一致的地方**：
> 「两颗果子分值相加 ≥3 + 优质及以上水」按本公式得 3+1+1+1 = **6 → 琼浆玉液**，不是臻品窖藏。
> 想稳定拿臻品窖藏，实际配方应为下列之一：
> - 果子 1+1（两颗小果）+ 优质水
> - 果子 2+1（正常+小果）+ **基础水**（即用 `water_ration` 直接灌）
> - 果子 2+2 + 幽灵水
>
> 如果实测确实是「≥3 + 优质水 = 臻品窖藏」，那说明还有第二个我未发现的加分/减分项（见 §10）。

---

## 8. 复现一瓶臻品窖藏：代码要依次做什么

### 方案 A（最忠实，走完整流程）

```csharp
// 1) 拿一个空酒瓶（工厂里已经 InitWineBottle 过）
GameItem bottle = DirectoryMaster.Item("wine_bottle", true);

// 2) 塞两颗果子（1 分 + 1 分）——必须真的成为 bottle 的子物品，StartFermentation 靠图遍历找
GameItem b1 = DirectoryMaster.Item("wine_berry", true);
WineHelper.SetBerryCondition(b1, 1, 0);          // condition=1, visualVariant=0
GameItem b2 = DirectoryMaster.Item("wine_berry", true);
WineHelper.SetBerryCondition(b2, 1, 0);
bottle.TryAcceptAll(b1);                          // 进 6x2 内容背包（允许 WINE_INGREDIENT_TAG）
bottle.TryAcceptAll(b2);

// 3) 放酵母（标准酵母 power=2）
GameItem yeast = DirectoryMaster.Item("wine_yeast", true);
bottle.TryAcceptAll(yeast);                       // 允许 WINE_YEAST_TAG

// 4) 灌 750ml「优质水」(grade 1 = 9900..9989)
WaterHelper.AddWater(bottle, 1, 750 * 1000, false, 0, 0, true);
//   若想 2+1 果子走基础水，改成 AddWater(bottle, 2, 750000, false, 0, 0, true)

// 5) 起酵（不要用 TryStartFermentation，它弹 UI）
WineHelper.StartFermentation(bottle);
//    此时 bottle: state=1, remaining=4, yeastPower=2, WINE_SCORE_INT=5

// 6) 两个"夜晚"后结算 —— 二选一
//    6a) 交给游戏：把瓶子放在玩家拥有(owned)的库存里，等两次 EndNight（onCycleEndSlotItemFunc 自动扣）
//    6b) 机器自己驱动：在自己的 onCycleEndSlotItemFunc 里 rem -= 2，rem<=0 时调 WineHelper.FinishFermentation(bottle)
WineHelper.FinishFermentation(bottle);
```

### 方案 B（最短，跳过所有交互）

```csharp
GameItem wine = WineHelper.CreateFinishedWineForDebug(5, false);   // wineScore=5 → 臻品窖藏
```

`CreateFinishedWineForDebug(int wineScore, bool counterfeit)` 的等价逻辑（很适合抄成机器产物逻辑）：

```csharp
static GameItem CreateFinishedWineForDebug(int wineScore, bool counterfeit) {
    var it = DirectoryMaster.Item("wine_bottle", true);
    it.ModifyTag(WINE_CONTAINER_STATE, s => { s.Enable(); s.SetInt(1); }, false);
    it.ModifyTag(WINE_SCORE_INT,       s => { s.Enable(); s.SetInt(wineScore); }, false);
    if (counterfeit) it.EnableTag(WINE_COUNTERFEIT_TAG, false);
    DrainBottle(it);
    it.DisableTag("LIQUID_CONTAINER_TAG", false);
    ContainerHelper.RemoveContainerTooltipTag(it);
    it.RemoveGameItemType("HOUSEHOLD_GOOD");
    it.CloseContentWindow(true);
    it.SetContentWindow(null);
    FinishFermentation(it);
    return it;
}
```

### 机器（自动化）实现要点

- 做一个 `GameItem` 机器：`SetSpriteAndShape` + `SetGameItemType("MACHINE")` + `EnableTag("CONTAINER_TAG")`
  + `EnableTag("STANDARD_MACHINE_TAG")` + `MachineryHelper.InitMachinery(machine, <耗电>, false)`
  + `MachineHelper.SetupBatterySlot(...)` + `MachineHelper.SetupModuleBay(...)`
  + `MachineHelper.CreateMachineryWindow(false, new[]{ MachineHelper.Panel(label, inv, role, true) ... })`
  + `machine.SetContentWindow(window)`
  —— 完全照抄 `WineHelper.InitWineBottle` 或 `MachineWineRack.AgeWell` 的结构即可。
- 周期驱动：给机器挂 `machine.onCycleEndSlotItemFunc`（每次 `PlayerStore.EndNight` 触发一次，
  和酒瓶自己发酵用的是同一个时钟，天然同步）。
- 只想"每 N 天产一瓶"而完全不碰液体系统：直接在 cycle 回调里
  `var w = WineHelper.CreateFinishedWineForDebug(5, false); machine.TryAcceptAll(w);`
- 若必须"逐颗果子判定"，就按 §7.3 自己算分，再用 `CreateFinishedWineForDebug(score,false)`。
- 想"复用玩家给的瓶子"：`WineHelper.ResetToEmptyBottle(bottle)` 复位后再走方案 A。

### 现成可参考的 UI / 交互代码

| 需求 | 看哪里 |
|---|---|
| 多物品放容器（6x2 网格 + 只允许特定 tag） | `WineHelper.InitWineBottle`（`new GameGridInventory(6,2)` + `ContainerHelper.AllowOnlyTaggedItemsOr`） |
| 机器面板/按钮 | `MachineHelper.Panel` / `CreateMachineryWindow` / `MachineryButton` |
| 从容器倒液体到另一容器（拖拽） | `WineHelper.SetupBottleLiquidTransfer` + `GameItemFunc.mayThisTargetItemFunc/canThisTargetItemFunc/onThisTargetItemFunc` 等 6 个委托 |
| 每天 tick 的机器 | `MachineWineRack.AgeWell`（`onCycleEndSlotItemFunc` + 电池/模块槽） |
| 果子品质显示 | `InsBerryUI.OnInit(GameItem)`（读 `BERRY_CONDITION_INT` / `BERRY_VISUAL_VARIANT_INT`） |
| 酒质 tooltip | `WineHelper.CreateTooltip`（发酵剩余天数、devMode 下显示 Wine Score / Yeast Power / Berry Quality） |

---

## 9. Mod 侧入口（顺带确认过签名）

```csharp
static class ModHook {   // 全是 static event / Action 字段
    Action OnGameLoadedInit, OnGameLoadedEarly, OnGameLoadedNormal, OnGameLoadedLate;
    Action OnShutterOpenedEarly, OnShutterOpenedLate, OnShutterClosedEarly, OnShutterClosedLate;
    Action OnGoingSleepEarly, OnGoingSleepLate, OnWakingUpEarly, OnWakingUpLate;
    Action OnHandlingNightlyServicesEarly, OnHandlingNightlyServicesLate;   // ★ 每夜结算前后
    Action<ModItemDirectory> OnModItemDirectoryInit;
    Action<RichTextBuilder, GameItem> OnCreateTooltipEarly, OnCreateTooltipLate;
    // … OnGenerateCustomer*/OnPlaceInventorInventoryItem*/OnNewGameStarted …
}

class ModHelper {
    static GameItem SpawnItem(string id);
    static GameItem SetItemOwnerShip(GameItem item, bool isOwn);
    static void MoveItemToInventory(GameItem item);
    static void AddDebugFunction(string modId, string category, string name, Action action, bool tall);
    static ModConfig GetConfig(string modId);
    // … 选项/掉落表/网络升级 …
}

class DirectoryMaster {
    static GameItem Item(string identifier, bool isOwned);
    static GameItem Item<T>(T identifier);          // 泛型（枚举）重载
    static T Create<T>(string identifier);          // 任意 Directory
}

class ItemDirectory {
    GameItem Empty();                               // 空白物品
    GameItem CreateEmptyItem(string identifier);    // 空白物品 + 指定 identifier
}
```

---

## 10. 没查清 / 有风险的点

1. **「≥3 + 优质水 = 臻品窖藏」与公式冲突**（见 §7.3 的 ⚠️）。
   我已把 `StartFermentation` 全量 IL 逐条核对过，加分项只有「果子分值 + ≥2 颗 +1 + 有酵母 +1 + 水质」，
   找不到第 5 项。可能性：
   - 玩家口中的"优质水"其实是 `water_ration` 灌出来的基础水（9800–9899 → 0 分）；
   - 或果子实际是 1+2 但其中一颗为 −1/0（被 `cond<0 → +1` 拉平）；
   - 或玩家看的是陈化后的名字（陈化不改档位，可排除）。
   → 建议实机用 F9 调试菜单/`PlayerStore.devMode` 打开 tooltip 看 `Wine Score` 实测值确认（devMode 下
   `WineHelper.CreateTooltip` 会直接打印 `Wine Score: N`）。
2. `WineHelper.BERRY_TAG` 具体被谁消费（除了 `InitBerry` / tooltip）没深挖，`PlantGeneticsHelper` /
   作物成熟度是否还间接影响 `BERRY_CONDITION_INT` 也未验证（目前看只有 `RollBloomberryCondition(health)` 一个来源）。
3. 「臻品窖藏」的中文名是按顺序对齐推断的（key 表 ↔ value 表），未在游戏内截图确认；不过
   modValue 单调性与名字语气完全吻合，可信度较高。
4. `ItemCondition.modValue`（+50 等）如何参与最终售价（`GetItemMiscValue` / `ComposeStagedValue`）
   没有展开——如果机器需要精确复现"卖价"，还要再读 `ItemFeature`/`GameItem.GetItemMiscValue`。
5. `Liquid.GetCurrentPartFromId("water")` 返回的 tag 名（推测形如 `liquid_water`）没逐字段核对；
   建议 mod 里不要硬编码，统一走 `Liquid.GetCurrentPartFromId(...)`。
6. 机器 mod 的**注册/解锁**（`ModItemDirectory.InitDirectory` 里 `Add(id, factory)`）没细看，
   目前 `ModItemDirectory` 只有空的 `InitDirectory()`，说明 mod 物品要走别的方式注册
   （`ModHelper.SpawnItem(id)` 之类），下个阶段需要单独调研。
