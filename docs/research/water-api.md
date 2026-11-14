# Probably Stolen — 水 / 液体容器 / 物品占格 / 机器形状 调研

> 只读调研。未修改游戏目录任何文件（本报告除外），未触碰 `Modding/`。
> 目标程序集：`Probably Stolen_Data/Managed/Assembly-CSharp.dll`（Unity 2021.3.45f2 / Mono）
> 图集资源：`Probably Stolen_Data/resources.assets`
> 上游报告：`brewing-api.md`、`container-ui-api.md`（本文不重复其内容，只做补充与纠错）

---

## 0. 工具链（沿用上一轮）

- `ilspycmd` / 自编译 .NET 工具均被本机 Device Guard 拦截 → **不要走这条路**。
- 元数据 + IL：`C:\Users\ciul4\AppData\Local\Temp\psd_tools\psd.py`（Python `dnfile` + `dncil`）。
  - `types <regex>` / `sig <regex>` / `il <类型名> <方法名regex>` / `consts <regex>` / `xr <regex>`
  - `il` 的类型名是**精确匹配且不带锚点**，要写 `Liquid` 而不是 `^Liquid$`。
  - `sig` 输出会被 `head` 截断，`WaterHelper` 有 80+ 行，别只取前 60 行。
- 图集切片尺寸：**UnityPy 1.25.4**（`python -m pip install UnityPy` 可装，本机可用）。
  读 `resources.assets` → `Sprite.m_Rect` 得到切片像素尺寸 → `/16` 得占格数。
- 中文名：直接在 `resources.assets` 里搜 UTF-8 中文串定位到本地化值表（key 表与值表分离，见 §4）。

---

## 1. 水存在哪：没有「Liquid tag」，是一组 `CURRENT_PART_*` int tag

### 1.1 结论

水**不是**一个叫 `Liquid` 的 tag。它是挂在 `GameItem.state`（`TagSystem`）上的
**每种液体一个独立 int tag**，名字由 `Liquid.currentPart` 决定。

```csharp
// Liquid.cs
public string identifier;    // "water"
public string part;          // "PART_"          + id.ToUpperInvariant()
public string currentPart;   // "CURRENT_PART_"  + id.ToUpperInvariant()

// Liquid.SetIdentifier(string id)
public void SetIdentifier(string id) {
    this.identifier  = id;
    this.part        = "PART_"         + id.ToUpperInvariant();
    this.currentPart = "CURRENT_PART_" + id.ToUpperInvariant();
}
```

所以：

| 液体 | `identifier` | 存储 tag（真正用的） | `part`（未用于存储） |
|---|---|---|---|
| 水 | `water` | **`CURRENT_PART_WATER`** | `PART_WATER` |
| 酒精 | `alcohol` | `CURRENT_PART_ALCOHOL` | `PART_ALCOHOL` |
| 重金属 | `heavy_metal` | `CURRENT_PART_HEAVY_METAL` | `PART_HEAVY_METAL` |
| … | … | `CURRENT_PART_` + ID 大写 | … |

> 我逐条读过 `WaterHelper` 的全部读/写路径（`GetLiquidParts` / `GetFreeCapacity` /
> `GetCurrentCapacity*` / `GetWaterPurity` / `TransferLiquid` / `AddLiquid` / `AddWater` /
> `EmptyContainer` / `RemoveMl` / `Remove` / `RemoveAndReturn`），**全部只用 `currentPart`**。
> `Liquid.part`（`PART_*`）在容器逻辑里没有出现，视为遗留字段，**mod 不要用它存水**。

⚠️ 注意：`PART_*` 与 `LIQUID_CONTAINER_CAPACITY` 不是同一套东西。
`PART_*` 是「这瓶曾经装过什么」意义上的标记，`CURRENT_PART_*` 才是「现在有多少」。

### 1.2 容器上的完整 tag 结构

```csharp
// 布尔/开关型（TagState.SetEnabled / IsEnabled）
LIQUID_CONTAINER_TAG            // "LIQUID_CONTAINER_TAG"        —— 是液体容器
FILTER_CONTAINER_TAG            // "FILTER_CONTAINER_TAG"        —— 带滤芯（可过滤）
GRADUATED_CONTAINER_TAG         // "GRADUATED_CONTAINER_TAG"     —— 刻度容器
LIQUID_CONTAINER_DROPPER_TAG    // "LIQUID_CONTAINER_DROPPER_TAG"—— 滴管

// int 型（TagState.SetInt / GetInt）
LIQUID_CONTAINER_CAPACITY  = 总容量，单位 µL（= ml × 1000）
                              // waterfall: 750ml 瓶 = 750000
LIQUID_CONTAINER_BASE_PRICE_TAG = 容器本身基础价
CURRENT_PART_WATER         = 水的量，单位 µL（int）
CURRENT_PART_ALCOHOL       = 酒精的量，µL
CURRENT_PART_<其余 34 种>   = 各自µL

// 字符串型
LAST_SCAN_RESULT_TAG  STRIP_TESTED_TAG   // 检测条/扫描结果，非水量
```

**没有「品质档」「纯度」的存储 tag。** 纯度是**运行时算出来的**：

```csharp
// WaterHelper.GetWaterPurity(GameItem g)  —— 真实纯度，0..10000
int water = g.GetTagReadonly(Liquid.GetCurrentPartFromId("water")).GetInt();  // CURRENT_PART_WATER
int total = Σ g.GetTagReadonly(l.currentPart).GetInt();                       // 全部液体之和
return total > 0 ? (int)((long)water * 10000 / total) : 0;
```

档位 = `WaterFeatureHelper.GetPurityArrayIndex(purity)`，纯函数，不落盘。

唯一「像纯度」的持久化值是 `ItemFeature.customIntValue1`
（category = `CATEGORY_WATER_PURITY`），但它只是**展示/被玩家扫描出的纯度快照**，
不参与真实纯度计算（`InitWaterFeature` 就是 `customIntValue1 = GetWaterPurity(item)`）。

### 1.3 单位

**1 单位 = 1 µL = 0.001 ml。** 全部容量/水量都是这个单位。

```csharp
GetTotalCapacityML(item)     => CAPACITY / 1000
GetCurrentCapacityML(item)   => Σ parts / 1000
GetCurrentCapacityUL(item)   => Σ parts
GetTotalVolume(item)         => Σ parts            // 与 UL 同义，原始单位
GetCurrentCapacity(item)     => Σ parts * 100 / CAPACITY   // ★ 这是 0..100 的百分比，不是容量！
GetFreeCapacity(item)        => CAPACITY - Σ parts
ItemDirectory 上限：capacityInMl > 2,147,483 会被 clamp 并 LogError
```

> ⚠️ **坑**：`GetCurrentCapacity()` 返回的是「装填百分比 0..100」，不是毫升数。
> 想要毫升用 `GetCurrentCapacityML()`，想要原始单位用 `GetTotalVolume()`。

---

## 2. `WaterHelper` / `WaterFeatureHelper` 全部 public 方法签名

### 2.1 `WaterHelper`（static class；元数据里 75 个 MethodDef，其中 71 个 `public`，含 `.ctor`/`.cctor`）

```csharp
// ---- 常量（const，从 Constant 表读出）----
public const string LIQUID_CONTAINER_CAPACITY        = "LIQUID_CONTAINER_CAPACITY";
public const string LIQUID_CONTAINER_BASE_PRICE_TAG  = "LIQUID_CONTAINER_BASE_PRICE_TAG";
public const string LIQUID_CONTAINER_TAG             = "LIQUID_CONTAINER_TAG";
public const string STRIP_TESTED_TAG                 = "STRIP_TESTED_TAG";
public const string FILTER_CONTAINER_TAG             = "FILTER_CONTAINER_TAG";
public const string GRADUATED_CONTAINER_TAG          = "GRADUATED_CONTAINER_TAG";
public const string LAST_SCAN_RESULT_TAG             = "LAST_SCAN_RESULT_TAG";
public const string LIQUID_CONTAINER_DROPPER_TAG     = "LIQUID_CONTAINER_DROPPER_TAG";
public const int    NUDKA_VOLUME_ML                  = 750;
public const int    NUDKA_ALCOHOL_PERCENT            = 45;
public const int    CONTAMINANT_PERCENTAGE_MAX       = 1000000;
public const int    DRY_TOLERANCE_UL                 = 100;
public const double LOWER_EDGE_SCALE                 = 0.08;   // (字节 0x3FB47AE147AE1400 → 0.08)
public static readonly HashSet<string> WaterBottleIds;
// = { "small_bottled_water", "bottled_water", "mini_bottle",
//     "large_bottled_water", "water_jug" }     // 仅用于播放"水瓶"音效

// ---- 初始化 / 查询 ----
public static void   InitLiquidContainerItem(GameItem item, int capacityInMl, int basePrice,
                                             bool isFilterContainer, bool isGraduated, bool isDropper,
                                             bool isTransparentContainer, int sheetPage);
public static void   UpdateWaterBottleSound(GameItem item);

// ★ 容量（单位 µL，见 §1.3）
public static int    GetFreeCapacity(GameItem item);
public static int    GetTotalCapacity(GameItem item);
public static int    GetTotalCapacityML(GameItem item);
public static int    GetCurrentCapacity(GameItem item);      // = 百分比 0..100
public static int    GetCurrentCapacityML(GameItem item);
public static int    GetCurrentCapacityUL(GameItem item);
public static int    GetTotalVolume(GameItem gameItem);      // Σ parts（µL）
public static string GetEstimatedVolume(int currentVolume, int totalVolume);
public static int    GetWaterPurity(GameItem gameItem);      // 0..10000
public static bool   IsEmpty(GameItem gameItem);
public static bool   IsFull(GameItem container);
public static bool   IsWet(GameItem container);
public static bool   IsDrain(GameItem gameItem);             // identifier == "drain"
public static bool   IsFaucet(GameItem gameItem);
public static bool   CanUseWaterContainer(GameItem gameItem);
public static bool   CanPourVolume(GameItem container, int volumeMl);   // ★ 够不够再装 N ml
public static bool   WillAcceptWater(GameItem targetItem, List<string> acceptedDisplays);

// ---- 转移 / 增删（★ 核心）----
public static void   TransferLiquid(GameItem sourceContainer, GameItem targetContainer);  // ★ 见 §3
public static int[]  GetLiquidParts(GameItem container);                                 // 每种液体的 µL 数组
public static int[]  SplitProportionally(int[] parts, int amount);                        // 按比例拆分
public static void   AddLiquid(GameItem waterContainer, string liquidId, int addAmount);
public static void   AddMaxLiquid(GameItem waterContainer, string liquidId);
public static void   RemoveMl(GameItem container, int volumeInMl);                        // ★ 精确扣 ml
public static void   Remove(GameItem container, int volumeInPart);                        // 精确扣 µL
public static List<KeyValuePair<string,int>> RemoveAndReturn(GameItem container, int volumeInPart); // ★ 扣并返回
public static void   EmptyContainer(GameItem gameItem);                                   // 全倒空
public static void   RemoveExcessLiquid(GameItem item);                                   // 超容则裁掉
public static int    SumParts(GameItem container, List<Liquid> liquids);
public static int    ShrinkProportionally(GameItem container, List<Liquid> liquids,
                                          int currentSum, int targetSum);
public static void   PurifyToBaseWater(GameItem container);

// ---- 造水（grade → 纯度区间，见 §4）----
public static void   AddWater(GameItem waterContainer, int grade, int addAmount,
                              bool useRange, int lowPara, int highPara, bool initWaterFeature); // ★
public static void   AddWaterOld(GameItem waterContainer, int grade, int addAmount);
public static int    RollNearLowerEdge(int low, int high);
public static void   FillWithPureWater(GameItem c);          public static void AddPureWater(GameItem c, int addAmount);
public static void   FillWithHighQualityWater(GameItem c);   public static void AddHighQualityWater(GameItem c, int addAmount);
public static void   FillWithBaseWater(GameItem c);          public static void AddBaseWater(GameItem c, int addAmount);
public static void   FillWithGhostwater(GameItem c);         public static void AddGhostWater(GameItem c, int addAmount);
public static void   FillWithGhostwaterHighEnd(GameItem c);  public static void FillWithGhostwaterLowEnd(GameItem c);
public static void   FillWithRustWater(GameItem c);          public static void AddRustWater(GameItem c, int addAmount);
public static void   FillWithRustWaterHighEnd(GameItem c);   public static void FillWithRustWaterLowEnd(GameItem c);
public static void   FillWithGutterflow(GameItem c);         public static void AddGutterflow(GameItem c, int addAmount);
public static void   FillWithNutrientSolution(GameItem waterContainer, int fillAmount);
public static void   PourSpirit(GameItem container, int volumeMl, int alcoholPercent);
public static bool   CanPourNudka(GameItem container);       public static void PourNudka(GameItem container);

// ---- 过滤 / 污染物 / 净化 ----
public static GameItem GetFilterItem(GameItem game);
public static int    HandleFilter(string identifier, int amount, GameItem sourceContainer, GameItem filter);
public static int    GetContaminantCount();
public static int    GetContaminantAmount(GameItem gameItem, string liquidID);
public static int    GetContaminantPercentage(GameItem gameItem, string liquidID);
public static void   OnWaterPurificationTabletAdded(GameItem waterContainer);
public static void   OnWaterPurificationTabletAdded2(GameItem waterContainer);
public static void   OnHydroponicNutrientTabletAdded(GameItem waterContainer);
public static void   OnUVUsed(GameItem waterContainer);
public static int    RemoveContaminantFromContainer(GameItem wc, string contaminantId,
                                                    int volumeToRemove, int minPercentage, int floorBaseVolume);
// ---- 杂项 ----
public static void   CreateWaterContainerTooltip(RichTextBuilder builder, GameItem item);
public static void   UpdateContainerGoodType(GameItem gameItem);
public static int    GetContainerPrice(GameItem gameItem);
public static string GetReactionString(string display);
public static void   SetLastScanResult(GameItem gameItem);
public static void   ResetLastScanResult(GameItem gameItem);
public static string GetLiquidContainerColor(GameItem gameItem);
public static void   SpawnWaterTestingKit();
```

### 2.2 `WaterFeatureHelper`（static class，public）

```csharp
// 档位索引
public const int PURE_WATER_INDEX       = 0;
public const int HIGH_QUALITY_WATER_INDEX = 1;
public const int BASE_WATER_INDEX       = 2;
public const int GHOST_WATER_INDEX      = 3;
public const int RUST_WATER_INDEX       = 4;
public const int GUTTERFLOW_WATER_INDEX = 5;

// 阈值（纯度 0..10000）
public const int PURE_WATER_PURITY        = 9990;
public const int HIGH_QUALITY_WATER_PURITY= 9900;
public const int BASE_WATER_PURITY        = 9800;
public const int GHOST_WATER_PURITY       = 9600;
public const int RUST_WATER_PURITY        = 9000;
public const int GUTTERFLOW_WATER_PURITY  = 3500;
public static readonly int[] WATER_PURITIES;      // {9990,9900,9800,9600,9000,3500}

// 显示
public static readonly string[] WATER_DISPLAY;    // 6 个本地化名（见 §4）
public static readonly string[] WATER_VALUE_DISPLAY; // {"99.9%+","99%-99.9%","98%-99%","96%-98%","90%-96%","90%-"}
public const string WATER_PURITY_INCONCLUSIVE = "WATER_PURITY_INCONCLUSIVE";

// 每升价格（用于 unitValue）
public const int PURE_WATER_PRICE_PER_LITER=200, HIGH_QUALITY=100, BASE=40,
                 GHOST=27, RUST=20, GUTTERFLOW=10;
public static readonly int[] WATER_PRICES;

// 方法
public static int         GetPurityArrayIndex(int purity);          // ★ 纯函数，唯一档位判定
public static ItemCondition GetConditionFromPurity(int purity);     // → ItemConditionList.conditionWaterPurity[idx]
public static ItemFeature   GetWaterFeature(GameItem gameItem);
public static void          InitWaterFeature(GameItem gameItem, bool isExpose);
public static void          UpdateWaterFeature(GameItem gameItem, bool isExpose);
public static void          UpdateWaterFeatureFake(GameItem gameItem, int fakePurity);
public static void          SetPresumedPurity(GameItem gameItem, int presumedPurity);
public static string        GetWaterDisplayName(GameItem gameItem);
public static string        GetWaterDisplayName(int purity);
public static string        GetCurrentWaterDisplay(GameItem gameItem);
public static int           GetWaterIndex(GameItem gameItem);
public static int           GetWaterPrice(int totalLiquidParts);
public static int           GetWaterPrice(GameItem gameItem);
public static bool          IsLabelledHazardousWater(GameItem gameItem);
public static Dialogue      GetWaterFeatureDialog(ItemFeature itemFeature);
public static void          OnLiquidContainerPourOutOf(GameItem gameItem);
public static void          OnLiquidContainerPourInto(GameItem target, GameItem source, bool wasEmpty);
public static void          OnLiquidChanged(GameItem target, bool exposeFeature);
public static void          HandleWaterCheckFlashlight(GameItem targetGameItem);
public static bool          CanBeCheckByFlashlight(GameItem gameItem);
public static ItemFeature   CreateFeaturePurityInconlusive();
public static void          HandleWaterCheckTestingStrip(GameItem targetGameItem);
public static string        FormatPurityPercentage(int purityValue);

// 判定实现（IL 还原）
public static int GetPurityArrayIndex(int purity) {
    for (int i = 0; i < WATER_PURITIES.Length; i++)
        if (purity >= WATER_PURITIES[i]) return i;   // 从高到低，第一个满足的
    return 5;
}
```

### 2.3 `Liquid`（液体定义表）

```csharp
public static List<Liquid> Liquids;                        // 36 项，InitLiquid() 构造
public static Dictionary<string, Liquid> LiquidDict;
public static List<Liquid> IngestableLiquids;
public static string GetCurrentPartFromId(string identifier);   // → "CURRENT_PART_WATER"
public static Liquid GetLiquidFromId(string identifier);
public static bool   IsLiquid(string id);
public string identifier, part, currentPart, displayName, color;
public int    baseMaxPurityFromMachine, basePurifierRemoveAmount, particleSize,
              filterDurabilityUsage, filterVolumeRemoved, colorMultiplier, metabolismRatePerDay;
public bool   canBeRemovedByPurifier, isEvaporative;
public double filterMinimumPurity, boilingPoint, lethality, euphoria;
public int    addictiveness;
```

36 个液体 id（`Liquid.LIQUID_*` const，全部小写）：
`water`, `heavy_metal`, `microbe`, `organic_waste`, `mineral`, `chemical_contaminant`,
`physical_contaminant`, `microplastic`, `dream_cap_mush`, `dream_cap_extract`, `plant_matter`,
`alcohol`, `caffeine`, `protein`, `fat`, `starch`, `sugar`, `coffee_mush`, `contraceptive`,
`zyanide`, `nightmare_extract`, `nightmare_cap_mush`, `chlorine`, `synaprest`, `fanta`,
`methylol`, `reducing_agent`, `acid`, `base`, `stabilizing_agent`, `debris`, `lipobond_agent`,
`livestock_steroid`, `livestock_immunity`, `wine_extract`, `nomore`

---

## 3. 怎么精确抽出 750ml ★★

### 3.1 有现成方法：`WaterHelper.TransferLiquid(source, target)`

**结论：不需要自己算，`TransferLiquid` 就是「按目标剩余容量、按比例转移」的正解。**
把目标换成一个**空酒瓶（容量 750000 µL）**，它就正好抽 750ml。

IL 还原（`WaterHelper.cs`，方法体 IL 约 0x215 字节）：

```csharp
public static void TransferLiquid(GameItem sourceContainer, GameItem targetContainer)
{
    bool wasEmpty = IsEmpty(targetContainer);
    if (!sourceContainer.IsTag(LIQUID_CONTAINER_TAG) ||
        !targetContainer.IsTag(LIQUID_CONTAINER_TAG)) return;

    int targetCap    = targetContainer.GetTagReadonly(LIQUID_CONTAINER_CAPACITY).GetInt();
    int targetSum    = 0;  foreach (var l in Liquid.Liquids) targetSum += targetContainer.GetTagReadonly(l.currentPart).GetInt();
    int targetFree   = targetCap - targetSum;
    if (targetFree <= 0) return;

    int sourceSum    = 0;  foreach (var l in Liquid.Liquids) sourceSum += sourceContainer.GetTagReadonly(l.currentPart).GetInt();
    if (sourceSum <= 0) return;

    int amount       = Math.Min(targetFree, sourceSum);         // ★ 只搬「目标装得下」的量
    GameItem filter  = GetFilterItem(targetContainer);
    int[] parts      = GetLiquidParts(sourceContainer);
    int[] move       = SplitProportionally(parts, amount);      // ★ 按比例拆，纯度不变

    for (int i = 0; i < Liquid.Liquids.Count; i++) {
        int src    = sourceContainer.GetTagReadonly(Liquid.Liquids[i].currentPart).GetInt();
        int toMove = move[i], toRemove = move[i];
        toMove     = HandleFilter(Liquid.Liquids[i].identifier, toMove, sourceContainer, filter);
        int tgt    = targetContainer.GetTagReadonly(Liquid.Liquids[i].currentPart).GetInt();
        ChemicalHelper.HandleReceiveLiquidTemp(targetContainer, toMove, sourceContainer);
        // b__0: target.currentPart = tgt + toMove   (skipValidate: true)
        targetContainer.ModifyTag(Liquid.Liquids[i].currentPart, s => s.Enable().SetInt(tgt + toMove), true);
        // b__1: source.currentPart = src - toRemove
        sourceContainer.ModifyTag(Liquid.Liquids[i].currentPart, s => s.Enable().SetInt(src - toRemove), true);
    }

    WaterFeatureHelper.OnLiquidContainerPourInto(targetContainer, sourceContainer, wasEmpty);
    WaterFeatureHelper.OnLiquidContainerPourOutOf(sourceContainer);
}
```

要点：

1. **`amount = min(目标剩余容量, 源总量)`** —— 源 1000ml / 5000ml 都无所谓，目标只有 750ml 空间，
   只会搬 750ml 走；源剩下 250ml / 4250ml 原样不动。
2. **按比例拆**（`SplitProportionally`），所以**纯度完全保持**：
   源是 9900 的优质水 → 瓶里还是 9900。所有污染物也按比例搬走（不会「只搬水不搬脏东西」）。
3. 参数顺序是 **(source, target)**，别写反。
4. 结束时自动调 `OnLiquidContainerPourInto/OutOf`，水特征（`CATEGORY_WATER_PURITY`）、
   显示名、`unitValue` 都会刷新；但**不会**调 `item.Validate()`，mod 里建议自己补一次。

### 3.2 推荐写法

```csharp
// 目标：从任意装水容器 src 抽取恰好 750ml 灌进空酒瓶
GameItem bottle = DirectoryMaster.Item("wine_bottle", true);   // 工厂内部已 InitWineBottle
                                                               // → LIQUID_CONTAINER_CAPACITY = 750000

if (WaterHelper.GetCurrentCapacityML(src) >= 750          // 源至少有 750ml
 && WaterHelper.CanPourVolume(bottle, 750))               // 瓶子的自由容量 >= 750ml
{
    WaterHelper.TransferLiquid(src, bottle);               // ★ 一步到位，正好 750000 µL
    bottle.Validate();
}
// 之后 WineHelper.StartFermentation(bottle) 即可（FERMENTATION_MIN_ML = 749，750 满足）
```

### 3.3 备选：需要「非满瓶」或「精确 mL 而不是填满」时

```csharp
// 从一个容器精确扣掉 N µL，并拿到"扣掉了什么" → 再塞进另一个容器
var taken = WaterHelper.RemoveAndReturn(src, 750 * 1000);   // List<KeyValuePair<string,int>>
foreach (var kv in taken)
    if (kv.Value > 0) WaterHelper.AddLiquid(bottle, kv.Key, kv.Value);
```

- `RemoveMl(container, ml)` / `Remove(container, µL)` / `RemoveAndReturn(container, µL)`
  三个方法内部都是：`target = ml*1000` → 若 `Σparts <= target` 就**全清空**（不是残留负数）
  → 否则 `SplitProportionally(parts, target)` 后逐项相减。都是精确、按比例、不会越界。
- 这条路适合「源只有 500ml、但我要先扣 300ml」这类场景；单纯灌满酒瓶用 `TransferLiquid` 更省事。

### 3.4 风险 / 注意

| 风险 | 说明 | 对策 |
|---|---|---|
| **`TransferLiquid` 只按"目标自由容量"转** | 它没有 volume 参数，所以**无法只转 300ml 进一个空着的 750ml 瓶** | 需要任意量时用 `RemoveAndReturn` + `AddLiquid`，或先 `AddLiquid` 占位 |
| **目标必须 `LIQUID_CONTAINER_TAG`** | 否则直接 `return`，静默失败 | `WaterHelper.CanPourVolume` 一并校验了 tag + 可用性 |
| **`LiquidContainerHelper` 是另一套系统** | 它用 `LIQUID_CONTAINER_CURRENT` / `LIQUID_CONTAINER_MAX` 两个 int 存**单一种液体**，`wine_bottle` **没有**这两个 tag → 调它的 `TransferLiquid` 会因为 `MAX=0` 直接失败 | 水/酒一律用 `WaterHelper.*`，不要用 `LiquidContainerHelper.*`（后者只服务 `pouch_hydration` 这类水袋） |
| **`GetCurrentCapacity()` ≠ 毫升** | 返回 0..100 百分比 | 用 `GetCurrentCapacityML()` |
| **`skipValidate: true`** | `TransferLiquid` 写 tag 时不校验，形状/价格不会自动刷新 | 结束后 `bottle.Validate()` |
| **源容器带滤芯** | `HandleFilter` 会按粒子大小扣水并消耗滤芯耐久，实际转移量可能 < 750ml | 灌酒瓶前先 `GetFilterItem(src) == null`，或干脆用干净水瓶 |
| **极小体积灌水会被向上取整抬高纯度** | `AddWater` 里 `purityAmount = ceil(addAmount*purity/10000)`，若 `addAmount` 很小，四舍五入可能跨过 9900 阈值 | 只在灌满 750ml 时用；750000 时误差 < 1/750000，可忽略 |
| **瓶子必须空** | 已有 100ml 时只会再进 650ml | 灌之前 `WaterHelper.IsEmpty(bottle)` 或先 `ResetToEmptyBottle` |

---

## 4. 六档水质：阈值、索引、名称、wineScore 加分

### 4.1 `AddWater` 的 grade → 纯度区间（IL 逐字）

```csharp
// WaterHelper.AddWater(container, grade, addAmount, useRange, lowPara, highPara, initWaterFeature)
(int low, int high) = grade switch {
    0 => (9990, 9999),
    1 => (9900, 9989),
    2 => (9800, 9899),
    3 => (9600, 9799),
    4 => (9000, 9599),
    _ => (3500, 8999),      // grade >= 5
};
if (useRange) { low = lowPara; high = highPara; }

// 取值方式：grade ∈ {1,2,3} 且 !useRange → RollNearLowerEdge（偏向下沿，均匀性差）
//           其它                          → RNG.GetRandomInt(low, high)
int purity = ...;

int purityAmount = (int)(((long)amount * purity + 9999) / 10000);   // ceil
if (purityAmount > amount) purityAmount = amount;
int contaminant = amount - purityAmount;                            // 差额全给污染物
```

所以 **grade 就是"档位索引"**，与 `WATER_PURITIES` 一一对应，`AddWater` 与
`GetPurityArrayIndex` 用的是同一套 6 个阈值，不会跨档。

### 4.2 六档总表（阈值 / 索引 / 名称 / 分值 / 价格 / modValue）

| idx | 阈值（≥） | 纯度区间 | 英文 enum 常量 | `identifier`（Liquid） | 中文名 | `WATER_VALUE_DISPLAY` | `WATER_QUALITY_SCORE` | 每升价 | `ItemCondition.modValue` |
|---|---|---|---|---|---|---|---|---|---|
| 0 | 9990 | 9990–9999 | `PURE_WATER_INDEX` | `mech_condition_pure_water` | **纯水** | `99.9%+` | **+1** | 200 | +100 |
| 1 | 9900 | 9900–9989 | `HIGH_QUALITY_WATER_INDEX` | `mech_condition_high_quality_water` | **优质水** | `99%-99.9%` | **+1** | 100 | +50 |
| 2 | 9800 | 9800–9899 | `BASE_WATER_INDEX` | `mech_condition_basewater` | **基础水** | `98%-99%` | **0** | 40 | 0 |
| 3 | 9600 | 9600–9799 | `GHOST_WATER_INDEX` | `mech_condition_ghostwater` | **幽灵水** | `96%-98%` | **−1** | 27 | −33 |
| 4 | 9000 | 9000–9599 | `RUST_WATER_INDEX` | `mech_condition_rustwater` | **铁锈水** | `90%-96%` | **−2** | 20 | −75 |
| 5 | 3500 | 3500–8999 | `GUTTERFLOW_WATER_INDEX` | `mech_condition_gutterflow` | **沟渠水** | `90%-` | **−3** | 10 | −90 |

> 中文名来源：`resources.assets` 的本地化值表里连续出现
> `纯水 / 优质水 / 基础水 / 幽灵水 / 铁锈水`（偏移 54784676–54784787），
> 与 key 表 `mech_condition_pure_water … mech_condition_gutterflow` 顺序一致；
> `沟渠水` 在另一段（54788008），与 `mech_condition_gutterflow` 对应。
> `modValue` 单调（+100 → −90）也印证档位顺序。

### 4.3 「grade 0/1 优质以上 = +1、grade 2 基础 = 0」的精确边界

`GetPurityArrayIndex` 是**从高到低找第一个满足 `purity >= 阈值` 的索引**，所以：

```
purity >= 9990        → idx 0 → +1
9900 <= purity < 9990 → idx 1 → +1
9800 <= purity < 9900 → idx 2 →  0
9600 <= purity < 9800 → idx 3 → −1
9000 <= purity < 9600 → idx 4 → −2
3500 <= purity < 9000 → idx 5 → −3
purity <  3500        → 仍然 idx 5（循环外 return 5）→ −3
```

也就是说：
- **「优质以上」= purity ≥ 9900**（含 idx 0 纯水 与 idx 1 优质），都 +1。
- **「基础」= 9800 ≤ purity < 9900**，0 分。
- **边界值本身归属于更高档**：正好 9900 → idx 1（不是 2）；正好 9800 → idx 2。
- **没有下限保护**：purity 可以是 0（纯污染物），仍归 idx 5 = −3。

常用灌水路径的实际落到哪一档：

| 调用 | 结果档位 | 加分 |
|---|---|---|
| `WineHelper.PourRationIntoBottle(bottle, ration)`（`water_ration`） | 内部 `AddWater(grade:2, 750*1000, useRange:false, …)` → 基础水 | **0** |
| `WaterHelper.FillWithBaseWater(bottle)` / `AddBaseWater(b, ml)` | grade 2 | 0 |
| `WaterHelper.FillWithHighQualityWater(bottle)` | grade 1 | +1 |
| `WaterHelper.FillWithPureWater(bottle)` | grade 0 | +1 |
| `WaterHelper.AddWater(b, 1, 750000, false, 0, 0, true)` | grade 1 | +1 |

---

## 5. 所有装水容器（id / 容量 / 形状）

全部来自各 `*ItemDirectory.InitDirectory()` 里的 `Add(id, () => Factory())`
+ 工厂方法里 `WaterHelper.InitLiquidContainerItem(item, capacityInMl, …)` 的实参。

| 物品 id | 容量 (ml) | 图集 / 切片 | 占格 | 备注 |
|---|---|---|---|---|
| `bottled_water` | **750** | `Items/water_container3` / `normal` | 1×4 | 标准水瓶，`WaterBottleIds` 成员 |
| `small_bottled_water` | 450 | `water_container3` / `small` | 1×3 | |
| `mini_bottle` | 250 | `water_container3` / `mini` | 1×2 | |
| `large_bottled_water` | **2000** | `water_container3` / `large` | 2×4 | |
| `water_jug` | **6000** | `Items/water_container4` / `xl` | **4×4** | 大桶 |
| `water_pitcher` | 3000 | `water_container4` / `pitcher` | 3×4 | **带滤芯**（`isFilterContainer=true`），`FILTER_CONTAINER_TAG`，子物品里放 `water_filter` |
| `water_tank` | **20000** | `Items/items_tool2` / `water_tank` | **4×4** | 家具级，可扩容；`WATER_TANK_TAG` + `HOUSEHOLD_GOOD` |
| `ras_water_tank` | 12000 | `Items/items_aquaculture` / `ras_tank` | 3×3 | 养殖机水箱；`RAS_WATER_TANK_TAG` + `IMPORTANT_TAG` |
| `1ml_dropper` | 1 | `water_container3` / `1ml` | 1×2 | 滴管 |
| `5ml_dropper` | 5 | `water_container3` / `5ml` | 1×2 | 滴管 |
| `25ml_cylinder` | 25 | `water_container3` / `25ml` | 1×2 | 量筒 |
| `50ml_cylinder` | 50 | `water_container3` / `50ml` | 1×2 | 量筒 |
| `100ml_cylinder` | 100 | `water_container3` / `100ml` | 1×2 | 量筒 |
| `med_bottle_red` | 250 | `Items/items_medical` / `med_bottle_red` | 1×2 | 药瓶，同样走液体容器 |
| `med_bottle_blue` | 250 | `items_medical` / `med_bottle_blue` | 1×2 | |
| `med_bottle_violet` | 250 | `items_medical` / `med_bottle_violet` | 1×2 | |
| `med_bottle_orange` | 250 | `items_medical` / `med_bottle_orange` | 1×2 | |
| `med_bottle_small_pink` | 150 | `items_medical` / `med_bottle_small_pink` | 1×1 | |
| **`wine_bottle`** | **750** | `Items/items_winemaking` / `wine_bottle` | **1×4** | 由 `WineHelper.InitWineBottle` 设容量 `750000`，**不是** `InitLiquidContainerItem` |

不属于 `WaterHelper` 液体容器的「水物品」（别搞混）：

| id | 说明 |
|---|---|
| `water_ration` (750ml) / `water_ration_small` (375ml) | **不是**液体容器。`GameItem` 只有 `WATER_RATION` 类型 + `SetupWaterRationPour` 拖拽回调；`WineHelper.PourRationIntoBottle` 走的是 `AddWater(grade:2,750000)` 然后 `ration.Destroy()` |
| `bottled_water_premium` | 纯 `FOOD` + `WATER` 类型，不可倒 |
| `pouch_hydration`（`UnusedDirectory`） | 走 `LiquidContainerHelper`（`LIQUID_CONTAINER_CURRENT` / `_MAX`），单液体，**与水的 multi-liquid 系统不互通** |
| `drain` | 排水口，`IsDrain()` 判定；有无限容量 |

> 注册表查找路径：`DirectoryMaster.GetIdentifierList<GameItem>()` 可枚举全部 id；
> 目录在 `FoodItemDirectory` / `AmenitiesItemDirectory` / `FurnitureItemDirectory` /
> `WineDirectory` 等，全部继承 `ItemDirectory`，`Add(string, Func<GameItem>)` 注册。

---

## 6. 物品占格实测（权威数据，非猜测）

### 6.1 形状是怎么来的

```csharp
// GameItem.BuildShapeFromSprite
public static GridShapeBuilder BuildShapeFromSprite(string atlasPath, string spritePath) {
    Sprite sp = RenderHandler.LoadFromAtlas(atlasPath, spritePath);   // Resources.LoadAll<Sprite>
    Rect r = sp.rect;
    int gs = UISettings.current.gridSize;         // ★ const 默认值 = 16（UISettings.ctor: ldc.i4.s 16）
    int mip = (int)Math.Sqrt(gs);                 // 16 → mip 4，正好等于「除以 16」
    int w = Math.Max(1, (int)r.width  / gs);
    int h = Math.Max(1, (int)r.height / gs);
    WarnIfSliceNotGridAligned(atlasPath, spritePath, r, gs);   // 非 16 倍数会打橙色 warning
    // 逐 16×16 块采样 alpha（在 mip 4 上取 (col,row) 一个像素），alpha>0 → 该格占位
    ...
}
```

**结论：占格数 = 切片像素尺寸 ÷ 16，四舍五入向下取整，最小 1×1。**
且形状是**逐格位图**——贴图某一格完全透明，那一格就不占位（方形图标 → 实心矩形）。

`SetSpriteAndShape(atlas, sprite)` 会先写 sprite 再 `BuildShapeFromSprite` + `SetShape`；
`SetShape` 会 `Clone()` 并 `ValidateShapeState()`。**后调用的会覆盖前一个**。

### 6.2 实测表（用 UnityPy 读 `Sprite.m_Rect` + 按 16 采样 alpha）

| 物品 | 图集 / 切片 | 像素 | **占格** | alpha 图案 |
|---|---|---|---|---|
| **葡萄** `wine_berry` | `Items/items_winemaking` / `wine_berry` | 32×32 | **2×2** ✅ 与用户说的一致 | `##/##` 实心 |
| **酒瓶** `wine_bottle` | `items_winemaking` / `wine_bottle` | 16×64 | **1×4** ✅ | `#/#/#/#` 实心 |
| 发酵中 `wine_fermenting` | `items_winemaking` / `wine_fermenting` | 16×64 | 1×4 | 实心 |
| 成品酒 `bottle0`…`bottle5` | `items_winemaking` / `bottleN` | 16×64 | 1×4 | 实心 |
| **酵母** `wine_yeast` | `items_winemaking` / `yeast` | 16×32 | **1×2**（不是 1×1） | 实心 |
| 高级酵母 `wine_superyeast` | `items_winemaking` / `superyeast` | 16×32 | 1×2 | 实心 |
| 非法酵母 `wine_yeast_red` | `items_winemaking` / `illegal_yeast` | 16×32 | 1×2 | 实心 |
| 酵母培养罐 `wine_yeast_infinite` | `items_winemaking` / `yeast_gen` | 32×32 | **2×2** | 实心 |
| 发酵栓 `wine_airlock` | `items_winemaking` / `fermentation_airlock` | 16×16 | 1×1 | 实心 |

### 6.3 原版机器作为家具摆放时的占格

| 机器 | id | 图集 / 切片 | 像素 | **占格** |
|---|---|---|---|---|
| **陈化酒架** `MachineWineRack` | `wine_rack` | `items_winemaking` / `wine_rack` | 48×48 | **3×3** |
| **搅拌机** `MachineBlender` | `blender` | `Items/items_tool2` / `blender` | 48×48 | **3×3** |
| **化工精炼机** `MachineChemFinisher` | `chem_finisher` | `items_tool2` / `chem_finisher` | 48×48 | **3×3** |
| 熔炉 `furnace` | `furnace` | `items_tool2` / `furnace` | 48×48 | 3×3 |
| 蒸发器 `evaporator` | `evaporator` | `items_tool2` / `evaporator` | 48×48 | 3×3 |
| 加热板 `heating_plate` | `heating_plate` | `items_tool2` / `heating_plate` | 48×48 | 3×3 |
| 水箱 `water_tank` | `water_tank` | `items_tool2` / `water_tank` | 64×64 | **4×4** |

> 原版机器的**商店占格完全是「贴图尺寸 ÷ 16」**，没有任何一处显式调 `SetShape`
> （`MachineWineRack.AgeWell()` 只有 `SetSpriteAndShape("Items/items_winemaking","wine_rack")`）。
> 所以「葡萄 2×2 / 酒瓶 1×4」的直觉是对的；「储水区 4×4」对应的是 `water_tank`（4×4）或
> `water_jug`（4×4），不是 UI 面板。

---

## 7. 机器形状怎么设：`SetSpriteAndShape` + `SetShape`

### 7.1 `MachineWineRack.AgeWell()` 里形状怎么来的（IL 还原）

```csharp
public static GameItem AgeWell()
{
    var (window, batterySlot, wineSlot, moduleGrid, manualSlot)
        = CreateMachineInventoryWindow(isDraggable: true, isCentered: true, minWidth: 150);
    MachineHelper.SetupManualSlot(manualSlot);
    SetupWineSlot(wineSlot);                       // 6×4 网格，只收 WINE_CONTAINER_TAG

    GameItem machine = ItemDirectory.CreateEmptyItem()
        .SetContentWindow(window)
        .SetSpriteAndShape("Items/items_winemaking", "wine_rack");   // ★ 只有这一句决定 3×3
    machine.name             = LocHelper.GetLocalizedItem("item_age_well_name");
    machine.shortDescription = LocHelper.GetLocalizedItem("item_age_well_desc");
    machine.unitValue        = 350L;
    machine.SetGameItemType("MACHINE");
    machine.EnableTag("CONTAINER_TAG");
    machine.EnableTag("STANDARD_MACHINE_TAG");
    AudioHelper.InitMetalMachine(machine);
    MachineHelper.SetupBatterySlot(batterySlot, machine);
    machine.ModifyTag("MACHINE_EFFICIENCY_RATIO_INT", s => { s.Enable(); s.SetInt(...); }, false);
    machine.ModifyTag("MACHINE_PERFORMANCE_RATIO_INT", s => { s.Enable(); s.SetInt(...); }, false);
    machine.EnableTag("MACHINE_PERFORMANCE_REDUCES_POWER_TAG", false);
    MachineryHelper.InitMachinery(machine, 24, false);           // 24 = 每晚耗电
    machine.onCycleEndSlotItemFunc = (m, inv, slot) => { /* 对槽内每瓶 OnAgeWine */ };
    return machine;
}
```

**形状 = 贴图切片的尺寸。没有第二处设置。** `gridSize` 是全局常量 16，
`wine_rack` 48×48 → 3×3。

### 7.2 要「自由指定 3×3」（贴图不一定是 48×48）

`GameItem.SetShape(GridShape)` 是 public，`GridShapeBuilder` 也是 public：

```csharp
public class GridShapeBuilder : GridShape {
    public GridShapeBuilder();                                  // 1×1，全 0
    public GridShapeBuilder(int width, int height);             // data = new byte[w*h] → ★ 全 0（不占格）
    public GridShapeBuilder(GridShape shape);
    public GridShapeBuilder(GridShapeBuilder shape);
    public GridShapeBuilder SetData(byte[] newData, int newWidth);
    public GridShapeBuilder SetData(string newData, int newWidth);   // "0101..." 字符串
    public GridShapeBuilder SetDataFill(int w, int h, byte b);
    public GridShapeBuilder SetDataFill(byte b);                     // Array.Fill(data, b)
    public GridShapeBuilder SetDataOutside(byte b);
    public GridShapeBuilder SetPosition(int minX, int minY);
    public GridShapeBuilder SetOrientation(int o);
    public GridShapeBuilder SetFlipped(bool f);
    public GridShape Build();  public GridShape Clone();
}

public GameItem SetShape(GridShape itemShape);   // 内部 Clone() + ValidateShapeState()
```

**写死一个 3×3 实心：**

```csharp
GameItem m = ItemDirectory.CreateEmptyItem()
    .SetContentWindow(window)
    .SetSpriteAndShapeFromMod(MyMod.Id, "brewer")          // 先给贴图（这步会顺手覆盖 shape）
    .SetShape(new GridShapeBuilder(3, 3).SetDataFill(1).Build());   // ★ 必须放在最后
```

非矩形（例如倒 L 形 3×3，占用 7 格）：

```csharp
.SetShape(new GridShapeBuilder("110110111", 3).SetDataOutside(0).Build())
//                        ↑ 行优先，'1' = 占格，'0' = 空
```

### 7.3 关键注意点

1. **顺序**：`SetSpriteAndShape*` 会覆盖 shape；`SetShape` 必须**在其之后**。
2. `new GridShapeBuilder(w,h)` 的 `data` 是**全 0**，不调 `SetDataFill` 得到的是「空形状」——物品会到处乱放。
3. 形状与贴图尺寸不一致时，**渲染位置按 shape 的包围盒走，贴图不会自动缩放**，
   会出现图标偏在一角 / 悬空。最稳的做法是**贴图切片本身就是目标格数 × 16**
   （3×3 → 48×48 PNG，切片必须 16 的整数倍，否则游戏打
   `[GameItem] SPRITE NOT ON GRID: ...` 橙色警告）。
4. 存档：`SaveItemNode.itemShape` / `itemModifiedShape` 会把 `GridShapeBuilder` 整个序列化，
   所以运行时设的自定义形状能存住，**但工厂函数每次读档也会重建一遍**，两者一致才安全。
5. `SetShape` 只影响**商店/背包里的占地**，与 `GameGridInventory(3,3)`（机器内部面板格子数）
   是两件完全不同的事。

---

## 8. 没查清 / 存疑

1. **`Liquid.part`（`PART_*`）到底谁在用**：我核对的 `WaterHelper` 全部容器读写路径都只用
   `currentPart`；`part` 未见消费者。但不排除 `FilterUI` / `SpriteHelper` 之类间接用到。
   → 结论仍然是「存水用 `CURRENT_PART_*`」。
2. **`WaterHelper.AddWater` 里 grade 0/1/3 共用同一个 `waterContaminantDistribution`**
   （grade 2 → `baseWaterContaminantDistribution`，grade 4 → `rustWaterContaminantDistribution`，
   ≥5 → `gutterflowContaminantDistribution`）。所以 **grade 0 与 grade 1 的污染物种类分布相同**，
   差别只在纯度阈值和价格。这一点与「纯水比优质水更干净」的直觉不完全一致，属于原版简化。
3. **`ItemCondition.modValue` 如何进入最终售价**（`GetItemMiscValue` / `ComposeStagedValue`）没展开；
   本报告只保证 `+100/+50/0/−33/−75/−90` 这几个权重本身。
4. **`WaterTankHelper` 的扩容机制**（用 scrap metal / metal ingot 提升 `LIQUID_CONTAINER_CAPACITY`）
   只看到 `Hint` 文案，未逐行读扩容逻辑；如果 mod 需要「可升级容量」，要另读 `WaterTankHelper`。
5. **`GetCurrentCapacity` 百分比** 的用途（`SpriteHelper.UpdateWaterContainerSprite` 水位贴图）
   只从调用点推断，未逐条验证。
6. **`mod` 能否直接调用 `ItemDirectory.Add` 注册家具**：本报告未新增结论，
   仍以 `container-ui-api.md` §5 的 `ModHook.OnModItemDirectoryInit` 为准。
