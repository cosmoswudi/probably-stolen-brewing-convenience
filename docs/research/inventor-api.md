# Probably Stolen — 人脉升级 / 杰克逊博士库存 / 定价 / 卖出回收 API 调研

> 只读调研。除本报告外，未修改游戏目录任何文件，未触碰 `Modding\`。
> 目标程序集：`Probably Stolen_Data/Managed/Assembly-CSharp.dll`（Unity 2021.3.45f2 / Mono）
> 时间：2026-11-08
>
> 关联报告：`container-ui-api.md`（ModHelper/ModHook/存档/机器 UI）、`brewing-api.md`（酿酒规则）

---

## 0. 方法与工具链（复现用）

沿用上一轮的 **Python `dnfile` + `dncil`**，脚本在 `%TEMP%\psd_tools\`（不在游戏目录）：

| 脚本 | 用途 |
|---|---|
| `psd.py` | `types / sig / il / consts / xr / ustr`，直接读元数据与 IL |
| `vis.py` | 读 `MethodDef.Flags`，判断 `public/private/internal` + `static` |
| `attrs.py` | 读 `CustomAttribute`，确认 `[ES3Serializable]` / `[ES3NonSerializable]` |
| `scan_price.py` | 全程序集扫描「谁给 `GameItem.unitValue` 赋了什么字面量」 |
| `loc10.py` | **新增**：UnityPy 读 Addressables 语言包，还原 `key → 简体中文`（含全部 `network_*` 文本） |

本地化资源位置（本次新发现，之前报告是硬凑的，现在是精确映射）：
```
Probably Stolen_Data/StreamingAssets/aa/StandaloneWindows64/
  localization-assets-shared_assets_all.bundle                          # key ↔ id
  localization-string-tables-chinese(simplified)(zh)_assets_all.bundle   # id ↔ 中文
```
`NetworkUpgrade` 表 = 119 条 key；`Id` 在 shared bundle 的 `<Table> Shared Data.m_Entries` 里。

---

## 1. 结论速览

| 问题 | 结论 |
|---|---|
| 酿酒相关的人脉升级 id | **`WINEMAKER`**（中文「退休酿酒师」，200，tab=`Referral`/引荐，无前置）|
| 判断是否已购买 | `NetworkUpgrade.IsWinemakerUnlocked()` 或 `ModHelper.IsNetworkUpgradeUnlocked("WINEMAKER")`，底层都是 `PlayerStore.Instance.networkUpgrade[id].state == 2` |
| `AddNetworkUpgrade` 能否「监听」原版升级 | **不能监听**，但可以用 `prerequisites: new[]{"WINEMAKER"}` 做**前置门槛**；`IsReady()` 会实时查前置是否已购买 |
| 能否新增自己的升级 | **能**，且**不需要**——本需求只要读 `WINEMAKER` 状态即可 |
| 博士库存怎么加货 | 订阅 `ModHook.OnPlaceInventorInventoryItemEarly`（`Action<List<GameItem>>`），在回调里自己调 `PlayerStore.Instance.AddDirectSellingItemToTable(item, false, false, false, 0)`。**回调给的是「玩家已拥有物品表」，不是「商店货架」**，必须自己加 |
| 定价 | `GameItem.unitValue`（属性，可直接赋值）/ `item.SetValue(888)`（等价）。买家实付 = `GetNegociatedValue()`；无零售加价时 ≈ `unitValue` + 内容物价值 |
| 原版机器价 | 100–1200，多数 200–400；**酒架 `wine_rack` = 350**，`dna_manipulator` = 1000，`machine_bay_ext` = 1200。888 偏贵但不离谱 |
| 刷新时机 | **每次「去博士店」重建**（`DrJacksonLocation.Arrive()`）。离开时 `ClearFrontInv()` 把未买走的货**全部 Destroy**。不会跨天累积，也不会有历史堆叠 |
| 「机器是否还存在」 | `EmporiumEntry.Instance.GetAllOwnedItems(false, true).Any(i => i.IsID("my_brewer"))` —— 原版自己就是这么去重的（`HashSet<string>` by identifier）|
| 卖出/丢弃的回调 | **没有 ModHook 事件**。靠「每次刷新时重新查所有权」最稳（原版同款）；要事件只能 Harmony / 包 `trashcanInvElement.onSlotAddItemEndFunc` |
| 卖出结算价 | 和买入同一条公式 `GetNegociatedValue()`（无折旧系数），仅受买家预算封顶 |
| 家具卖出/丢弃时内部物品 | **跟着消失，不会掉回玩家身上**。`GameItemElement.Destroy()` → `DestroyContentWindow()` → 递归 `Destroy()` 所有子物品。卖出时物品+内容一起进 `soldElement`（已售暂存）；进垃圾桶后次日 `ClearTrash()` 连内容一起销毁 |

---

## 2. 人脉升级（Network Upgrade）

### 2.1 类型全貌

`NetworkUpgrade`（`System.Object`，非 MonoBehaviour）。关键字段：

```csharp
public class NetworkUpgrade {
    [ES3Serializable]    public string subtitle;
    [ES3Serializable]    public int    cost;
    [ES3Serializable]    public string id;
    [ES3Serializable]    public bool   isRepeatable;
    [ES3Serializable]    public int    cooldownCurrent;
    [ES3Serializable]    public int    cooldownDuration;
    [ES3Serializable]    public List<string> prerequisite;   // 前置升级 id
    [ES3Serializable]    public bool   lockInDemo;
    [ES3Serializable]    public int    state;                // ★ 0=不可用 1=可购买 2=已购买
    [ES3NonSerializable] public string tab;                  // 分组大 tab（空 = 默认 tab）
    [ES3NonSerializable] public string iconKey;
    [ES3NonSerializable] public string source;               // mod id
    [ES3NonSerializable] public bool   recommended, hidden, heldUntilUsed;
    [ES3NonSerializable] public Func<string> titleProvider, subtitleProvider,
                             descriptionProvider, alreadyBoughtProvider;
}
```

静态表（都在 `NetworkUpgrade` 上）：

```csharp
public static List<NetworkUpgrade> modDefinitions;                                   // 所有 mod 注册的
public static Dictionary<string, NetworkUpgrade> definitions;                        // 合并后的全量定义（缓存）
public static List<NetworkUpgrade> definitionOrder;                                  // UI 顺序
```

### 2.2 状态机与「已购买」判定（★核心）

```csharp
// NetworkUpgrade
public bool IsUnlocked()  => state == 2;
public bool IsHavePrerequisite() { /* 空 list => true；否则每个前置都 TryGetRuntime+IsUnlocked */ }
public bool IsReady()     => IsHavePrerequisite()
                          && !(cooldownDuration != 0 && cooldownCurrent != 0)
                          && state == 1;
public int  GetCost()     { /* cost * Difficulty.Rules.upgradePriceMultiplier ... */ }

public void Unlock() {
    state = 2;
    if (!string.IsNullOrEmpty(id) && NetworkUpgradeList.UnlockActions.TryGetValue(id, out var a) && a != null)
        a();                                              // ★ 原版/自定义升级的实际效果
    if (cooldownDuration != 0) {                          // 可重复购买型（如“清除证据”）
        var def = GetDefinition(id) ?? this;
        if (!heldUntilUsed) state = 1;
        cooldownCurrent = cooldownDuration;
    }
    ModHook.FireOnNetworkUpgradeUnlocked(id);              // ★ 唯一触发点
}

public static bool IsUnlocked(string id)
    => PlayerStore.Instance.networkUpgrade.ContainsKey(id)
       && PlayerStore.Instance.networkUpgrade[id].IsUnlocked();

public static bool IsWinemakerUnlocked()
    => PlayerStore.Instance.networkUpgrade["WINEMAKER"].IsUnlocked();   // 注意：无 ContainsKey 保护
```

存储位置：

```csharp
// PlayerStore
[ES3Serializable] public Dictionary<string, NetworkUpgrade> networkUpgrade;   // ★ 随存档，per-save
```

> 判定「已购买」的唯一真值 = `PlayerStore.Instance.networkUpgrade["WINEMAKER"].state == 2`。
> `IsWinemakerUnlocked()` 直接索引不判空，**在 `OnGameLoadedEarly` 之前调用会 NRE**；安全写法见 §2.5。

读档回填（决定 mod 升级能否在已有存档里出现）：

```csharp
// PlayerStore.LoadGame() 里调用
public static void AddMissingAfterLoad() {
    if (PlayerStore.Instance.networkUpgrade == null) PlayerStore.Instance.networkUpgrade = new();
    foreach (var kv in InitUpgradeDict())                  // ★ 含 modDefinitions
        if (!PlayerStore.Instance.networkUpgrade.ContainsKey(kv.Key))
            PlayerStore.Instance.networkUpgrade.Add(kv.Key, kv.Value);
    ...
}
```
- 调用点只有两处：`PlayerStore.LoadGame()` 和 `WildNetworkUI.Open()`。
- `AddNetworkUpgrade` 若在「已有存档已加载」的情况下被调用，`RegisterModUpgrade` 会**立刻**把它塞进 `PlayerStore.Instance.networkUpgrade`（见 §2.4），否则等下次 Load/打开网络界面时补。

### 2.3 `ModHelper.AddNetworkUpgrade` 逐字签名

```csharp
public static void ModHelper.AddNetworkUpgrade(
    string modId, string id, int cost, string subtitle,
    Func<string> title, Func<string> description, Action onUnlock,
    string tab = null, string iconKey = null, string[] prerequisites = null,
    int cooldownDays = 0, Func<string> subtitleLabel = null,
    Func<string> alreadyBought = null, bool recommended = false);
```

参数含义 + IL 证据（`ModHelper::AddNetworkUpgrade`）：

| 参数 | 落点 | 说明 |
|---|---|---|
| `modId` | `NetworkUpgrade.source` | 同时是 `RemoveNetworkUpgrades(modId)` 的键；空则 `LogError` 直接 return |
| `id` | `.ctor(id)` | 全局唯一；与原版 id 撞名会被 `InitUpgradeDict` 丢弃并 warning（`"...which already exists; ignored"`）|
| `cost` | `cost` | 基础价，实际价经 `GetCost()` 乘难度/技能修正 |
| `subtitle` | `subtitle` | **同时是卡片分组名**（见 §2.6）；null → `""` |
| `title` / `description` | `titleProvider` / `descriptionProvider` | 每帧取；返回 null 会自动回落到 `network_<id>_title` / `_desc` 本地化 key |
| `onUnlock` | `NetworkUpgradeList.UnlockActions[id]` | `Unlock()` 里调用；null 则从字典移除 |
| `tab` | `tab` | 大 tab id；null → `""`（默认 tab）|
| `iconKey` | `iconKey` | 图标 key |
| `prerequisites` | `prerequisite.AddRange(...)` | ★ 前置 gid 数组；`IsHavePrerequisite()` 实时查 |
| `cooldownDays` | `cooldownDuration` + `isRepeatable = cooldownDays > 0` | >0 时可重复购买 |
| `subtitleLabel` | `subtitleProvider` | 副标题的本地化覆盖 |
| `alreadyBought` | `alreadyBoughtProvider` | 已买后显示的文案 |
| `recommended` | `recommended` | 置顶「推荐」卡 |

`AddNetworkUpgrade` 强制 `state = 1`（即可购买），所以只要 `prerequisites` 满足就立刻出现。

### 2.4 注册流程（mod 侧）

```csharp
// NetworkUpgrade.RegisterModUpgrade
public static void RegisterModUpgrade(NetworkUpgrade definition, Action onUnlock) {
    if (definition == null || string.IsNullOrEmpty(definition.id)) { Debug.LogError("[NetworkUpgrade] RegisterModUpgrade needs a definition with an id"); return; }
    modDefinitions.RemoveAll(d => d.id == definition.id);   // 覆盖注册
    modDefinitions.Add(definition);
    if (onUnlock != null) NetworkUpgradeList.UnlockActions[definition.id] = onUnlock;
    else                  NetworkUpgradeList.UnlockActions.Remove(definition.id);
    InvalidateDefinitions();                                // definitions = null
    if (PlayerStore.Instance != null && PlayerStore.Instance.networkUpgrade != null
        && !PlayerStore.Instance.networkUpgrade.ContainsKey(definition.id))
        PlayerStore.Instance.networkUpgrade.Add(definition.id, definition.CopyForRuntime());
}

public static int RemoveModUpgrades(string source);   // 按 source 移除，返回条数
```

`CopyForRuntime()` 会把 `tab / iconKey / source / recommended / hidden / 4 个 provider / prerequisite` 全拷一份。
`state` 是 `[ES3Serializable]`，`tab/iconKey/source/providers` 是 `[ES3NonSerializable]` ——
所以**存档里只留状态，UI 元数据每次都从 `definitions` 现取**（`NetworkUpgradeLayout.Build` 的入参是
`NetworkUpgrade.GetDefinitionsInOrder()`，不是 runtime 字典），改定义不会因为读档丢 tab。

### 2.5 推荐写法（本需求实际用不到新增升级）

```csharp
// 唯一需要的判断
static bool BrewingUnlocked()
{
    var store = PlayerStore.Instance;
    return store != null && store.networkUpgrade != null
        && NetworkUpgrade.IsUnlocked(NetworkUpgrade.WINEMAKER);   // 或 "WINEMAKER"
}
```
- `ModHelper.IsNetworkUpgradeUnlocked("WINEMAKER")` 已经内建了上述空判，**直接用这个更省事**。
- `NetworkUpgrade.IsWinemakerUnlocked()` 少了 `ContainsKey` 保护，只在 `OnGameLoaded*` 之后可用。

如果之后确实想加自己的升级（例如「酿酒便利化 I」），写法：

```csharp
ModHelper.AddNetworkUpgrade(
    modId: MyMod.Id,
    id: "ps_brewer_convenience",
    cost: 300,
    subtitle: "Referral",                       // 归到「引荐」卡片
    title:       () => ModHelper.GetLocalized(MyMod.Id, "nu_brewer_title"),
    description: () => ModHelper.GetLocalized(MyMod.Id, "nu_brewer_desc"),
    onUnlock:    () => { /* 例如直接给玩家发一台机器 */ },
    prerequisites: new[] { NetworkUpgrade.WINEMAKER },   // ★ 必须买过「退休酿酒师」
    recommended: false);
```
> ⚠️ 不要用 `onUnlock` 做「机器开始出现在博士店」——那是**一次性**回调；出货判断请放到 §3.2。

### 2.6 UI 分组规则（影响 `subtitle` 的选值）

```csharp
// NetworkUpgradeLayout.Build
tab  = def.tab ?? "";                 // 大 tab
card = def.tab + "\n" + (def.subtitle ?? "");   // tab 内的卡片
tabs.GetOrAddTab(def.tab);            // 空字符串 = 默认 tab
```
- 原版 id→`subtitle`（=卡片名，中文来自 `network_*_subtitle` 之外的 subtitle 表）：
  `Introduction`（红魔鬼酿酒厂/化学家/药剂师/矿业）、`Favor`（恩惠）、`Informant`（线人）、
  `Referral`（引荐）、`Service`（服务）、`Store`（店铺）、`Water`（水）、`Supplier`（供货商）、
  `Gunsmith`、`Hydroponic`、`Aquaculture`、`Endorsement`。
- `tab = "Advanced"` 的只有 `HYPERTECH_SMUGGLER` 和 `ENDORSEMENT*`。

### 2.7 原版全部人脉升级（id / 价格 / 卡片 / 前置 / 中文名）

中文名取自 `network_<ID>_title`；「—”表示该升级的 cost 是运行时算的（股东/背书类）。

| id | cost | subtitle | 前置 | 中文名 |
|---|---|---|---|---|
| `BREWER` | 200 | Introduction | — | 红魔鬼酿酒厂 |
| `CHEMIST` | 250 | Introduction | — | 下层区化学家 |
| `PHARMA` | 300 | Introduction | — | 上层区药剂师 |
| `MINER` | 250 | Introduction | — | GP采矿合作社 |
| `BREWER_SHAREHOLDER` | — | Introduction | — | 小股东：红魔鬼酿酒厂 |
| `MINER_SHAREHOLDER` | — | Introduction | — | 小股东：GP采矿合作社 |
| `CHEMIST_SHAREHOLDER` | — | Introduction | — | 小股东：下层区化学家 |
| `PHARMA_SHAREHOLDER` | — | Introduction | — | 小股东：上层区药剂师 |
| `LANDLORD_GRACE` | 100 | Favor | — | 租金宽限 |
| `SEC_GRACE` | 250 | Favor | — | 豁免权 |
| `INSPECTION_INFORMANT` | 300 | Informant | — | 盘查预警服务 |
| `WILD_FIXER` | 350 | Informant | — | 王尔德的善后人 |
| `WILD_FIXER2` | 450 | Informant | WILD_FIXER | 王尔德的善后人 II |
| `WILD_FIXER3` | 550 | Informant | WILD_FIXER2 | 王尔德的善后人 III |
| `EVIDENCE_REMOVAL` | 500 | Informant | — | 清除证据 |
| `BUY_REFERAL` | 200 | Referral | — | 买家推荐服务 |
| `SELL_REFERAL` | 200 | Referral | — | 卖家推荐服务 |
| `CRIMINEL_NETWORK` | 350 | Referral | — | 罪犯人脉 |
| `POWER_HIJACK` | 300 | Service | — | 偷接私电 |
| `POWER_HIJACK2` | 600 | Service | POWER_HIJACK | 偷接私电 II |
| `POWER_HIJACK3` | 1000 | Service | POWER_HIJACK2 | 偷接私电 III |
| `COMMERCIAL_POWER` | 350 | Service | — | 商用级供电单元 |
| `SHOWCASE_I` | 200 | Store | — | 展示柜扩建 I |
| `SHOWCASE_II` | 500 | Store | SHOWCASE_I | 展示柜扩建 II |
| `GUTTERFLOW_TAP` | 250 | Water | WATER_TRADER | 沟渠水直供 |
| `RUSTWATER_TAP` | 350 | Water | GUTTERFLOW_TAP | 铁锈水直供 |
| `RUINED_MACHINE_UNLOCK` | 300 | Referral | — | 废弃机器拆解 |
| `RETIRED_FARMER` | 350 | Hydroponic | — | 退休农夫 |
| `RETIRED_GUNSMITH` | 650 | Gunsmith | — | 退休枪匠 |
| `GUN_PERMIT_I` | 300 | Gunsmith | — | 武器许可证 I |
| `GUN_PERMIT_II` | 600 | Gunsmith | GUN_PERMIT_I | 武器许可证 II |
| `GUN_PERMIT_III` | 900 | Gunsmith | GUN_PERMIT_II | 武器许可证 III |
| `RETIRED_CHEMIST` | 1000 | Referral | — | 退休化学家 |
| `RETIRED_RANCHER` | 750 | Referral | — | 养殖 |
| `WATER_TRADER` | 300 | Water | — | 水资源交易 |
| `PLANT_DNA` | 1250 | Hydroponic | RETIRED_FARMER | 植物基因 |
| `DOSSIER_UPGRADE` | 300 | Store | — | 档案箱升级 |
| `TRASHCAN_UPGRADE` | 300 | Store | — | 垃圾桶升级 |
| `VENDING_MACHINE` | 1200 | Store | — | 自动售货机 |
| `VENDING_FOUNTAIN` | 1200 | Store | — | 自动售水机 |
| `AQUACULTURE_II` | 1200 | Aquaculture | AQUACULTURE | 水产养殖 II |
| `AQUACULTURE` | 400 | Aquaculture | WATER_TRADER | 水产养殖 |
| **`WINEMAKER`** | **200** | **Referral** | **—** | **退休酿酒师 ★** |
| `MARKETPLACE_REQUESTS` | 250 | Service | — | 市场委托 |
| `JACKSON1` | 400 | Supplier | — | 杰克逊投资 I |
| `JACKSON2` | 650 | Supplier | JACKSON1 | 杰克逊投资 II |
| `JACKSON_SPECIAL_STOCK` | 1250 | Supplier | JACKSON2 | 周一特供 |
| `RENOVATION1` | 500 | Store | — | 店铺翻新 I |
| `RENOVATION2` | 750 | Store | RENOVATION1 | 店铺翻新 II |
| `RENOVATION3` | 1000 | Store | RENOVATION2 | 店铺翻新 III |
| `HYPERTECH_SMUGGLER` | 2500 | Referral (tab=Advanced) | — | 超科技走私贩 |
| `ENDORSEMENT1/2/3` | — | Endorsement (tab=Advanced) | — | 王尔德的背书 I/II/III |

**和酿酒相关的那条 = `WINEMAKER`「退休酿酒师」**：
> 立即获取一套入门级酿酒设备。一名退休酿酒师将在第二天到访，随后每周到访一次，出售酿酒用品和设备。（解锁酿酒玩法。）

它的 `onUnlock`（`NetworkUpgradeList/<.cctor>b__136_35`）发的是：
`wine_yeast ×3`、`wine_bottle ×3`、`wine_berry ×2`、`water_ration ×1`、`magnifier ×1`、`wine_book ×1`
—— **不含任何机器**，机器（酒架）本来就在博士那里随机卖。

> 注意别选错：`BREWER`「红魔鬼酿酒厂」是**啤酒**批发商（`BEER_CASE` 那条线），与酿酒机无关；
> `RETIRED_RANCHER`「养殖」是水产。真正解酿酒的是 `WINEMAKER`。

### 2.8 `ModHook.OnNetworkUpgradeUnlocked`

```csharp
public static event Action<string> ModHook.OnNetworkUpgradeUnlocked;   // 参数 = upgradeId
// 唯一触发点：NetworkUpgrade.Unlock() 末尾 → ModHook.FireOnNetworkUpgradeUnlocked(this.id)
```
- 时机：**购买成功的那一刻**（`state=2` 已写入、`UnlockActions` 已执行、冷却已设置之后）。
- 读档**不会**触发；`MarkUnlockedSilently`（`state=2` 无声）**也不会**触发。
- `SafeInvoke` 包裹，单个订阅者抛异常不影响别人。
- 想覆盖「读档时已经买过」的情况，不要只靠这个事件（见 §2.5 的直接查询）。

---

## 3. 杰克逊博士的库存与定价

### 3.1 两个入口的完整签名与时序

```csharp
// ModHook（public static event / Action 字段）
public static event Action<List<GameItem>> OnPlaceInventorInventoryItemEarly;
public static event Action<List<GameItem>> OnPlaceInventorInventoryItemLate;

// PlayerStore（实例方法，全部 public）
public void AddDirectSellingItemToTable(string identifier, bool isOwend, bool isStolen, bool ignorePerk, int forcedHeat);
public bool TryAddDirectSellingItemToTable(string identifier, bool isOwend, bool isStolen, bool ignorePerk, int forcedHeat);
public void AddDirectSellingItemToTable(GameItem gameItem, bool isOwend, bool isStolen, bool ignorePerk, int forcedHeat);
public bool TryAddDirectSellingItemToTable(GameItem gameItem, bool isOwend, bool isStolen, bool ignorePerk, int forcedHeat);
public void AddDirectSellingItemToTableMiddle(GameItem gameItem, bool isOwend);
public void AddDirectToWeightedTable(GameItem gameItem, bool isOwend);

// 原版内部小工具（public static！可直接调）
public static void StoreClientList.AddIfNotOwn(string itemId, HashSet<string> ownedIds);
```

> 上一轮报告里写的 `AddDirectSellingItemToTable(GameItem)` **不存在**，实际是 5 参数版本。

**★ 关键点：`OnPlaceInventorInventoryItem*` 传的 `List<GameItem>` 是「玩家已拥有的物品」，不是商店货架。**

```csharp
// StoreClientList.PlaceInventorInventory(bool isVisitingPlayerStore)
int maxOffers = 66;                                                       // IL_000c ldc.i4.s 66
List<GameItem> playerItems = EmporiumEntry.Instance.GetAllOwnedItems(false, true);  // ★ 玩家已有
HashSet<string> ownedIds = new();                                         // ← 由上面建 id 集合
foreach (var it in playerItems) ownedIds.Add(it.identifier);              // ★ 用 identifier 去重

ModHook.FireOnPlaceInventorInventoryItemEarly(playerItems);               // ← 传的是 playerItems
ReactorHelper.TryOfferStockReactor(playerItems);

if (!isVisitingPlayerStore) {
    if (!ownedIds.Contains("satchel"))  PlayerStore.Instance.AddDirectSellingItemToTable("satchel", false,false,false,0);
    ... // 背包、mirage_projector、storage_bay、machine_bay、recharger、furnace、fridge、desequencer ...
}
... // 加密模块 / 各类模组 / 耗材 / water_filter / ...

if (NetworkUpgrade.IsWinemakerUnlocked()) {                               // ★★ 原版同款范式
    if (RNG.Roll(33))                                                     // 33% 概率
        PlayerStore.Instance.AddDirectSellingItemToTable(PreBuiltItemHelper.CreateAgeWell(), false,false,false,0);
    if (RNG.Roll(20))                                                     // 20% 概率
        PlayerStore.Instance.AddDirectSellingItemToTable("wine_yeast_infinite", false,false,false,0);
}

StoreClientList.GuaranteeLockedCrateKeycardItems(playerItems, ownedIds);
ModHook.FireOnPlaceInventorInventoryItemLate(playerItems);                 // ← 仍然是 playerItems
```

要点：
1. **`Early` / `Late` 都是「时机钩子」**，参数是只读的玩家物品快照；往里面 `Add/Remove` 只可能影响后续原版判断（`GuaranteeLockedCrateKeycardItems`），**不会自动上架**。
2. 上架必须自己调 `AddDirectSellingItemToTable`。
3. `Early` 时 `ownedIds` 集合**已经建好了**，所以改 `playerItems` 影响不到原版的去重；但**你自己的**判断要自己算（或传自己的 HashSet 给 `AddIfNotOwn`）。
4. `AddDirectSellingItemToTable` 的真实行为 = **立即 `DirectoryMaster.Item(id, true)` 造一个物品并塞进 `EmporiumEntry.Instance.frontInvinvElement`**：

```csharp
public bool TryAddDirectSellingItemToTable(GameItem gameItem, bool isOwend, bool isStolen, bool ignorePerk, int forcedHeat)
{
    if (gameItem == null) { Debug.LogWarning("AddDirectSellingItemToTable called with null item (identifier not registered in this build variant?)"); return false; }
    // ... LLDISTRUSTED / BM_DISTRUSTED 派系违禁品过滤（ignorePerk=true 可跳过）...
    GeneralHelper.SetItemOwned(gameItem, isOwend);                       // ← 货架上的货是「未拥有」
    if (isStolen && PlayerStore.Instance.isStolenItemIntroduced) StolenHelper.InitStolenItem(gameItem, forcedHeat);
    if (!GraphUtils.TryAcceptAllOrDestroy(EmporiumEntry.Instance.frontInvinvElement, gameItem, "front counter full"))
        return false;                                                     // ★ 位置不够会销毁，静默失败
    StartingPerkContent.HandleGamblingAddictionItemAdded(gameItem);
    return true;
}
```

### 3.2 库存刷新时机 / 会不会重复堆叠

```csharp
// DrJacksonLocation.Arrive()
PlayerStore.Instance.playerLocation = 1;
EnsureSheetTablet();
EmporiumEntry.Instance.frontInvWindow.Show();
EmporiumEntry.Instance.backInvWindow.Show();
MapUIManager.Instance.TransferBackInvToCounter();
EmporiumEntry.Instance.ExpandFrontCounterGrid();          // 前台 18x4 → 14x21（72 → 294 格）
if (AfterhourEvents.IsActive("special_stock")) StoreClientList.PlaceInventorSpecialStock();
else                                          StoreClientList.PlaceInventorInventory(false);
...
```

```csharp
// EmporiumEntry
public void ExpandFrontCounterGrid() => frontInvinvElement.SetShape(14, 21);
public void ResetFrontCounterGrid()  => frontInvinvElement.SetShape(18, 4);

// DrJacksonLocation.Leave()
foreach (var inv in new[]{ frontInvinvElement, backInvinvElement })
    foreach (var it in inv.childItems)
        if (GeneralHelper.IsItemOwned(it)) { it.CloseContentWindow(true); inv.Expel(it); kept.Add(it); }  // 自己买的带走
EmporiumEntry.Instance.ResetFrontCounterGrid();
EmporiumEntry.Instance.ClearFrontInv();                   // ★ 剩下的（未买走的货）全部 Destroy()
EmporiumEntry.Instance.ClearBackInv(true);
MapUIManager.Instance.TransferCounterToBackInv();
foreach (var it in kept) GraphUtils.EmporiumTryAdd(EmporiumEntry.Instance.backInvWindow, it);
```

结论：
- **刷新单位 = 一次「到店」**（不是每天）。每次 `Arrive()` 重建；`Leave()` 全部清空重来。
  → **不存在跨次访问的累积 / 重复堆叠**。
- 但 `PlaceInventorInventory` 有**两个调用点**：
  `DrJacksonLocation.Arrive()` 和 `StoreClientList.CreateInventor` 的 lambda `b__23_0`（博士反过来造访玩家店时，
  此时 `isVisitingPlayerStore = true`，会跳过前半段背包/设备块，但**后半段的 WINEMAKER 块仍会执行**，货照样进玩家店前台）。
  → 同一「场景」里理论上存在二次触发的可能，**建议加一次「货架上是否已有同 id」的扫描**（见 §3.4）。
- 前台容量：博士店里 14×21 = 294 格，装几十件货完全够；`TryAcceptAllOrDestroy` 满了会**静默销毁**并 `ReportUnplaced`。
  → 用 `TryAddDirectSellingItemToTable` 至少能拿到 bool 返回值。
- **想「清空后重建」**：现成逻辑就是 `EmporiumEntry.Instance.ClearFrontInv()`（会 Destroy 全部），
  但**不要**在 `Early` 里调用它——博士正在往里面摆货。需要主动重置的话放在 `OnPlaceInventorInventoryItemLate` 里再摆一遍也行，但没必要。

`PlaceInventorSpecialStock()`（`special_stock` 事件期间）**完全不去重**，`EnergyCreditBreeder` 等直接 Add 两遍。
如果我们的机器在这个事件期间也要卖，得自己判重。

### 3.3 定价

```csharp
// GameItem
public long unitValue { get; set; }                 // ← 直接赋值即可
public GameItem SetValue(long num) { unitValue = num; return this; }   // ★ 等价于 unitValue = num

// 买家实付（PlayerStore.BuyItem）
int cost = 0; cost += (int)item.GetNegociatedValue();
OnItemBought(item, cost);
playerCash -= cost;
MoveDealItemToInventory(item);
```

```csharp
public long GetNegociatedValue()
{
    long complexe = GetComplexeItemValue();
    long total   = unitValue + complexe;
    long innate = 0, market = 0; double deal = 1.0;
    AccumulateFeatureStages(ref innate, ref market, ref deal, skipEventFeatures: false);
    int misc = GetItemMiscValue(useMarkup: true, withChild: true, includeEvents: true,
                                forceMarkup: false, inheritClientMods: true);
    return ComposeStagedValue(total, innate, market, deal, misc, WaterHelper.GetContainerPrice(this));
}
// GetItemMiscValue = lateUnitValue + (未打 IGNORE_CHILD_VALUE 时的内容物价值)
// ComposeStagedValue = Round( (totalBase * (1+innate/100) + marketScaled + misc) * deal )
```

- 裸机器（无 feature / condition / 事件 / 内容物）：`innate=0, market=0, deal=1, misc=0`
  → **实付 ≈ `unitValue`**。
- `PlayerStore.retailMarkup` **不参与** `GetNegociatedValue`（只在 `GetCurrentValue(useRetailMarkup:true)` 里加，
  且 `IsApplyMarkUp()` 要求「物品在后仓 + 已拥有 + 当前顾客接受加价」，那是玩家转卖时的零售加价）。
- `withChild = true` ⇒ **机器里装的酒会加价**。要让「售价固定 888」精确成立，出货时给机器
  `EnableTag("IGNORE_CHILD_VALUE")`（会走 `GetItemMiscValue` 里把 `withChild` 置 false 的分支），
  或者接受「空机器 = 888」这个近似。
- 若想完全绕开一切修正，可 `item.unitValue = 888` + 不加任何 `ItemFeature`，实测基本就是 888。

### 3.4 推荐实现（博士库存加货 + 手定价）

```csharp
// mod OnEnable
ModHook.OnPlaceInventorInventoryItemEarly += EnsureBrewerAtJackson;

static void EnsureBrewerAtJackson(List<GameItem> playerItems)
{
    if (!ModHelper.IsNetworkUpgradeUnlocked("WINEMAKER")) return;            // ① 人脉门槛

    // ② 玩家已拥有（含背包/货架/垃圾桶/暗格）→ 不再出售
    if (playerItems.Any(i => i.IsID(MyMod.BREWER_ID))) return;

    // ③ 同一次到店里防止二次触发导致重复
    var front = EmporiumEntry.Instance.frontInvinvElement;
    if (front != null && front.childItems.Any(i => i.IsID(MyMod.BREWER_ID))) return;

    // ④ 上架
    GameItem machine = DirectoryMaster.Item(MyMod.BREWER_ID, true);
    machine.SetValue(888);                                                    // = unitValue = 888
    machine.EnableTag("IGNORE_CHILD_VALUE");                                  // 可选：锁定 888
    if (!PlayerStore.Instance.TryAddDirectSellingItemToTable(machine, isOwend: false,
                                                             isStolen: false, ignorePerk: false, forcedHeat: 0))
        Debug.LogWarning("[MyMod] 博士前台放不下机器，本次跳过");
}
```

> 原版等价写法（更短）：`StoreClientList.AddIfNotOwn(MyMod.BREWER_ID, ownedIds)`，
> 但 `ownedIds` 要自己从 `playerItems` 建；`AddIfNotOwn` 内部用 `ItemSpawner.Spawn(id)` 造物品，**没法设价格**，
> 所以还是手写更好（价格必须在 `Add` 之前设好）。

### 3.5 原版机器价格参考（全量扫描 `GameItem.unitValue` 赋值）

| 物品 id / 工厂 | unitValue |
|---|---|
| `wine_rack`（陈化酒架 `MachineWineRack.AgeWell`） | **350** |
| `dehydrator`/`furnace`（`MachineFurnace`） | 200 |
| `blender`（`MachineBlender`） | 200 |
| `bottle_printer`（`MachineBottlePrinter`） | 100 |
| `box_dispenser` | 250 |
| `chem_finisher` | 300 |
| `evaporator` | 300 |
| `desequencer` | 350 |
| `printer` | 350 |
| `moisture_farm` | 350 |
| `water_purifier` | 250 |
| `hydroponic` | 300 |
| `heating_plate` | 250 |
| `fridge` | 100 |
| `recharger` | 150 |
| `powerblock` | 200 |
| `storage_coordinator` | 400 |
| `deal_maker` | 400 |
| `mirage_projector` | 400 |
| `auto_butcher` | 400 |
| `dna_manipulator` | **1000** |
| `machine_bay_ext`（`ShipSystemDirectory`） | **1200** |
| `security_alarm` | 150 |
| `backpack_large` / `medium` / `satchel` | 650 / 450 / 150 |
| `luxury_supply_crate` | 300 |

- 价格**硬编码在 C# 工厂里**（`item.unitValue = N`），不在 Addressables 数据里。
- 分布：中位数 ≈ 275，P90 ≈ 400，天花板 1200。
- **888 的量级**：比所有常规机器贵一倍多，和 `dna_manipulator`(1000) 同档，低于 `machine_bay_ext`(1200)。
  「高端但没离谱」，和 `JACKSON_SPECIAL_STOCK`（周一特供「价格较高的稀有物品」）的定位吻合。

---

## 4. 「机器是否还存在」检测 + 卖出/回收

### 4.1 遍历所有已拥有物品

```csharp
// PlayerStore（public）
public List<GameItem> FindAllItem(bool isOwn);        // isOwn=true → Where(GeneralHelper.IsItemOwned) + Distinct()

// EmporiumEntry（public static Instance）
public List<GameItem> GetAllOwnedItems(bool isBurglary, bool includeAfterhour);
public List<GameItem> GetAllOwnedItemsShallow(bool isBurglary);
public bool IsFrontCounterEmpty();  public bool IsFrontCounterEmptyNonOwned();
```

`FindAllItem(true)` 覆盖的窗口：
`frontInvWindow, backInvWindow, backInvinvElementCounter, invWindow, showcaseWindow, trashInvWindow,
 docInvWindow, hiddenWindow, afterhourWindow, vendingFountainWindow, vendingMachineWindow`

`GetAllOwnedItems(isBurglary:false, includeAfterhour:true)` 覆盖的网格：
`invElement(24x10 店铺地面), backInvinvElement(后仓), backInvinvElementCounter, frontInvinvElement(前柜台),
 docInvElement, showcaseElement(橱窗), trashInvElement(垃圾桶槽), vendingMachineElement, vendingFountainElement,
 afterhourWindow(includeAfterhour 时), hiddenElement(isBurglary=false 时)`

两者底层都是 `GraphUtils.FindAllChildrenType(...)` —— **递归**，所以：
- ✅ 背包里的机器（背包是容器物品，挂在地面网格上）算
- ✅ 机器里装的葡萄/酒瓶也算（会出现在扁平列表里，注意别把它们当机器）
- ✅ 橱窗 / 后仓 / 前柜台 / 暗格 / 垃圾桶 算
- ✅ 时段地点（afterhour）掉落物算

**两个 API 都会漏掉的容器**（`EmporiumEntry` 上有、但两个函数都没遍历）：
`hirelingInv`（雇员背包 10x10）、`soldElement`（已售暂存）、`bazarLeftinvElement`、
`drainInvElement`/`faucetElement`/`cassettePlayerElement`（水槽排水口、水龙头、磁带机单格）、
`swapBufferElement`、`afterhourPocketSlot*`、`responseInventory*`。

> 实用建议：**用 `GetAllOwnedItems(false, true)` 作为「机器是否存在」的判定**（和原版去重口径一致）。
> 想更严格就把 `EmporiumEntry.Instance.hirelingInv.ChildItems` 也并进来。
> `soldElement` 缺席是**好事**——卖掉的机器不算「还存在」，才会重新上架。

```csharp
static bool PlayerStillHasMachine()
{
    var e = EmporiumEntry.Instance;
    if (e == null) return false;
    bool owns(GameItem i) => i != null && i.IsID(MyMod.BREWER_ID);
    if (e.GetAllOwnedItems(false, true).Any(owns)) return true;
    if (e.hirelingInv != null && e.hirelingInv.childItems.Any(owns)) return true;   // 补漏
    return false;
}
```

`GameItem.identifier` 是 `public string get`；`IsID(string)`、`uniqueId` 也都是 public。
`GeneralHelper.IsItemOwned(item)` / `SetItemOwned(item, bool)` 是判定所有权的地方（`IS_OWNED_TAG`）。

### 4.2 卖出 / 回收的钩子清单

| 时机 | 可用的点 | 签名 | 备注 |
|---|---|---|---|
| 玩家买入 | `PlayerStore.OnItemBought` | `public void OnItemBought(GameItem gameItem, int cost)` | **不是事件**，需 Harmony patch；内部会 `SetItemOwned(item,true)` + `machineryBoughtCount++` |
| 顾客买走（玩家卖出） | `PlayerStore.SellItem` | `public void SellItem(GameItem item)` | 同上；末尾 `SetItemOwned(item,false)` → `SoldItem.TrackItem(item, client, price)` |
| 售出记录 | `SoldItem.TrackItem` | `public static void TrackItem(GameItem gameItem, StoreClient storeClient, int soldPrice)` | public static，可 Harmony postfix 拿到「刚卖掉的东西」 |
| 进垃圾桶 | `EmporiumEntry.Instance.trashcanInvElement.onSlotAddItemEndFunc` | `Action<GameItem, GameInventory, SlotMarker>` | ★ **可包装**（原版只用来刷垃圾桶贴图，见下）|
| 从垃圾桶取出 | 同上 `onSlotRemoveItemEndFunc` | `Action<GameItem, GameInventory, SlotMarker>` | |
| 夜间清空垃圾桶 | `PlayerStore.ClearTrash` | `public void ClearTrash()` | 只在 `HandleJanitorial()` 且雇了保洁时才跑 |
| 子物品被取出 | `GameItemFunc.onUnloadChildItemFunc` | `Action<GameItem, GameItem>` | 容器子物品移除时；不是「机器被卖」 |
| **ModHook 里有没有？** | **没有** | — | 45 个事件里**没有任何 sold / trash / destroy 事件** |

垃圾桶回调的现成实现（可安全包装，但**必须链式调用原委托**，否则垃圾桶贴图不刷新）：

```csharp
// MiscItemDirectory.Trashcan() 里：grid.onSlotAddItemEndFunc = b__1 → SpriteHelper.UpdateTrashcanSprite(grid, trashcan)
var grid = EmporiumEntry.Instance.trashcanInvElement;      // public GameGridInventory
var prevAdd = grid.onSlotAddItemEndFunc;
grid.onSlotAddItemEndFunc = (item, inv, slot) => {
    if (item.IsID(MyMod.BREWER_ID)) OnBrewerTrashed(item);  // 你的逻辑
    prevAdd?.Invoke(item, inv, slot);                      // ★ 保留原逻辑
};
```

**推荐：完全不依赖这些钩子。** 因为「机器还在不在」可以随时从所有权推出来，
而唯一需要这个信息的地方就是「博士上货时」，那时重新查一次即可（原版 `satchel`/`backpack` 就是这套）。
钩子的价值仅在于「立刻给玩家反馈」（如提示「你的酿酒机没了」）。

### 4.3 卖出 / 回收的结算价

```csharp
// PlayerStore.SellItem(item)
int price = 0; price += (int)item.GetNegociatedValue();       // ★ 与买入同一条公式
int original = price;
if (client != null && client.useClientBudget) {
    if (client.GetBudget() < price) price = client.GetBudget();          // 买家预算封顶
    client.ModBudget(-price);
    if (client.GetBudget() < 0) client.OverrideBudget(0);
}
client?.OnItemBought(item, price);            // 注意：这里调的是 StoreClient 的同名方法
playerCash += price;
StoreReputation.OnItemTraded(client.clientFaction, original, price, client, isPlayerSold: true, item);
int boughtFor = item.IsTag("BOUGHT_PRICE_TAG") ? item.GetTagReadonly("BOUGHT_PRICE_TAG").GetInt() : 0;
RunStats.OnItemSold(item, price, boughtFor, client);
GeneralHelper.SetItemOwned(item, false);
SoldItem.TrackItem(item, client, price);       // → 复制一条 SoldItem 记录 → MoveToSoldBag(item)
```

- **没有折旧系数**：卖价 = 买价（同为 `GetNegociatedValue()`），只有买家预算封顶。
  所以 888 买入 / 888 卖出（若顾客预算够）。原版机器同理，属于游戏设计。
- 垃圾桶回收**不给钱**，只是 `ClearTrash()` 里销毁。
- `BOUGHT_PRICE_TAG` 由 `GeneralHelper.TrackBuyPrice(item, cost)` 在 `OnItemBought` 里写入，
  用于 `RunStats` 统计「这笔赚了多少」。

### 4.4 `SoldItem` 的去向（影响「是否还存在」）

```csharp
// SoldItem.TrackItem 末尾
MoveToSoldBag(gameItem);                       // → EmporiumEntry.Instance.soldElement（GameGridInventory）
PlayerStore.Instance.soldItemsTracker.Add(soldItem);

// SoldItem.MoveToSoldBag
var bag = EmporiumEntry.Instance.soldElement;
if (gameItem.parentInventory != null) gameItem.parentInventory.Expel(gameItem);
GraphUtils.TryAcceptAllOrDestroy(bag, gameItem, ...);      // 物品本体（含内容物）进「已售暂存」
```
- 卖掉的机器**连同内容物原样搬进 `soldElement`**，还存在于存档（`soldInvJSON`），
  但**不在** `FindAllItem` / `GetAllOwnedItems` 的遍历范围内，也不 `IsItemOwned`。
  → 我们的存在性检测判它「不存在」 = 会重新上架。✅ 符合需求。
- `Leave()` 里 `ResetFrontCounterGrid()` + `ClearFrontInv()`：博士的未售货是**未拥有**，会被 Destroy。

---

## 5. 家具被卖出 / 丢弃时，内部容器里的东西怎么办

### 5.1 Destroy 的链条（决定性证据）

```csharp
// GameItemElement.Destroy()（GameItem.Destroy 的实现体）
public void Destroy()
{
    if (destroyed) return;
    destroyed = true;
    if (parentInventory != null) parentInventory.Expel(this);
    if (contentWindow != null) contentWindow.Hide();
    if (uniqueId != 0) PlayerStore.Instance?.contentWindowPositions?.Remove(uniqueId);
    DestroyContentWindow();                 // ★
    if (fakeMiddle != null) Object.Destroy(fakeMiddle.gameObject);
    Object.Destroy(handler.gameObject);
}

// GameItem.DestroyContentWindow()
public void DestroyContentWindow()
{
    var w = contentWindow;
    if (w == null) return;
    foreach (var child in GraphUtils.FindChildrenType<GameItem>(w, null, null))
        child.Destroy();                    // ★ 递归：葡萄 / 酵母 / 酒瓶全部销毁
    w.RemoveParent(this);
    contentWindow = null;
    w.Destroy();
}
```

`CloseContentWindow(bool includeChildren)` **只隐藏窗口**，不动子物品：

```csharp
public void CloseContentWindow(bool includeChildren)   // includeChildren 只影响「子物品的窗口是否也隐藏」
{ contentWindow?.Hide(); if (!includeChildren) return; foreach (var c in FindAllChildrenType<GameItem>(...)) c.contentWindow?.Hide(); }
```

### 5.2 三个场景逐一

| 场景 | 调用链 | 内部物品结局 |
|---|---|---|
| **卖给顾客** | `SellItem` → `PerformOnSoldCheck(item, client)`：`item.CloseContentWindow(true)`（**只隐藏**）→ 各种违禁品检查 → `SoldItem.TrackItem` → `MoveToSoldBag` → `soldElement` | **不销毁**。机器 + 内部物品原样进入「已售暂存」，玩家**拿不回来**（除非游戏后续的退货机制，`SoldItem.CanReturn`） |
| **丢进垃圾桶** | `GraphUtils.TryAcceptAll` 把机器塞进 `trashcanInvElement`（10×10 网格）→ 每晚 `HandleJanitorial()` → `ClearTrash()`：`item.CloseContentWindow(true)` + `item.Destroy()` | **全部销毁**。`Destroy()` → `DestroyContentWindow()` → 递归 Destroy 子物品 |
| **卖出后再被销毁 / 读档丢失** | 同 `Destroy()` | 同上，全部销毁 |

`ClearTrash()` 的真实实现：

```csharp
public void ClearTrash()
{
    garbageLevel = 0;
    if (EmporiumEntry.Instance.trashcanInvElement == null) return;
    var all = GraphUtils.FindAllChildrenType<GameItem>(EmporiumEntry.Instance.trashcan, null, null);
    if (all.Count == 0) return;
    for (int i = all.Count - 1; i >= 0; i--) {
        if (GeneralHelper.IsHazardousWaste(all[i])) continue;      // 危险废物保留
        if (IsKeptInTrash(all[i])) continue;                       // smugglers bay 相关保留
        all[i].CloseContentWindow(true);
        all[i].Destroy();                                          // ★ 内容物一起没
    }
}
```
`ClearTrash` **只在 `HandleJanitorial()` 里、且「雇了保洁服务」时**执行：

```csharp
public void HandleJanitorial()
{
    if (!StoreService.GetJanitorialService().unlocked) return;
    if (!StoreService.GetJanitorialService().isUsing) {                 // 没雇保洁 → 只涨脏乱度
        garbageLevel = Math.Min(garbageLevel + 2, 10);
        NightLog.StorefrontNotCleaned(NightLog.GetGarbageDescription(garbageLevel));
        return;
    }
    ...
    ClearTrash();                                                       // ★
    ...
}
```
→ **没雇保洁时，丢进垃圾桶的机器会一直留在垃圾桶里**（仍然 `IS_OWNED_TAG`），
  此时我们的存在性检测会认为「机器还在」，不会重新上架。这与「被垃圾桶回收（=第二天被清走）」在语义上一致，
  只是「回收」被推迟到玩家雇了保洁的那一晚。

### 5.3 结论 / 要不要自己写「清空物品」

- **内部物品不会掉回玩家身上，也不会被转移到别处**。卖出=进已售暂存（玩家不可见），垃圾桶=直接销毁。
- 因此如果你希望「机器被卖掉/回收时把里面的葡萄和酒还给玩家」，**必须自己写**。
  最省事的挂点：包装垃圾桶的 `onSlotAddItemEndFunc`（进桶时把内容物 `Expel` 到后仓），
  卖出侧没有事件可包，只能 Harmony `PlayerStore.SellItem` 前缀，或者在 `SoldItem.TrackItem` 的 postfix 里
  「从 `EmporiumEntry.soldElement` 里找到刚卖掉的那台，取走内容物」。
- 反过来说，**如果你的机器按「机器是消耗品、里面东西跟着走」处理，就不需要写任何清理逻辑**——
  原版 `wine_rack`（6×4 酒格）也是这个行为：卖掉/清掉时里面的酒一并消失。

---

## 6. 推荐实现骨架（完整时序）

```csharp
public sealed class MyMod : IMod
{
    public const string Id = "my_brewer_mod";
    public const string ItemId = "ps_brewer";

    void OnEnable() {
        ModHook.OnModItemDirectoryInit += dir => dir.Add(ItemId, () => MyBrewer.Create());
        ModHook.OnPlaceInventorInventoryItemEarly += OnJacksonStock;
    }

    void OnJacksonStock(List<GameItem> playerItems)
    {
        // ① 人脉门槛：只有买过「退休酿酒师」才卖
        if (!ModHelper.IsNetworkUpgradeUnlocked(NetworkUpgrade.WINEMAKER)) return;
        // ② 玩家已有（含背包/货架/垃圾桶/暗格）→ 不再出售
        if (playerItems.Any(i => i.IsID(ItemId))) return;
        // ③ 同一次到店防重复
        var front = EmporiumEntry.Instance.frontInvinvElement;
        if (front != null && front.childItems.Any(i => i.IsID(ItemId))) return;
        // ④ 上架 + 定价
        GameItem m = DirectoryMaster.Item(ItemId, true);
        m.SetValue(888);
        m.EnableTag("IGNORE_CHILD_VALUE");
        PlayerStore.Instance.TryAddDirectSellingItemToTable(m, false, false, false, 0);
    }
}
```
> 这套逻辑**不需要** `ModHelper.SetModData*`（没有额外状态要存），
> 也**不需要**任何卖出/垃圾桶钩子——「有没有」永远从所有权现算。
> 唯一需要注意：玩家买走后机器被放进 `hirelingInv` 时原版口径会漏判（§4.1），如需严格可自己补一条。

---

## 7. 未查清 / 风险

1. **`isVisitingPlayerStore = true` 时货进哪儿**：`PlaceInventorInventory(true)` 会跳过前段背包块，
   但后段（含 WINEMAKER 块、我们的注入）仍执行，货进 `frontInvinvElement`（此时是玩家店的前台）。
   这条路径由 `StoreClientList.CreateInventor` 的 lambda 触发，**没实测过**；建议加 §3.4 的二次判重。
2. **`AddDirectSellingItemToTable` 在 `LLDISTRUSTED` / `BM_DISTRUSTED` 派系下的过滤**：只在 `ignorePerk=false`
   且当前顾客属于对应派系时，对「违禁品」才拦截。普通机器不受影响，但如果你把机器标成违禁品要留意。
3. **`GetNegociatedValue` 的 `inheritClientMods` 具体分支**没逐条读完（`GetItemMiscValue` 本体只有
   `lateUnitValue + 内容物`，顾客修正应该在 `AccumulateFeatureStages` 里）。888 是否精确等于柜台显示价，
   建议实机用 `PlayerStore.devMode` / F9 价格调试面板核对一次（`DebugPanel.priceDebugTMP2` 会打印
   `NegociatedValue / base / innate% / market% / misc / deal x`）。
4. **`SoldItem.CanReturn` 的退货机制**（顾客退货把物品还回来）没展开——如果存在，卖掉的机器可能回到玩家手里，
   那「已售」并不等于永久消失。目前只确认 `TrackItem` 会记 `CanReturn` 字段，未找到消费点的时间线。
5. **`special_stock` 事件期间**（`PlaceInventorSpecialStock`）不去重、直接 Add 两遍，
   如果我们的机器那时也要卖，得自己判重（§3.2 已述）。
6. **`hirelingInv` / `bazarLeftinvElement`** 等容器不在任何「全量遍历」里，属于游戏自身的口径缺口，
   不是本次能改的。
