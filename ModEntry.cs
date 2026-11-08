using System;
using System.Collections.Generic;
using System.Linq;

namespace BrewingConvenience
{
    /// <summary>
    /// 模组入口。Init / OnEnable 都在主菜单执行，此时还没有存档，
    /// 所以这里只做注册与订阅，任何存档数据都留到 OnGameLoaded* 之后处理。
    /// </summary>
    public class ModEntry : IMod
    {
        public const string ModId = "brewing_convenience";

        /// <summary>原版「退休酿酒师」人脉升级 id —— 买过它，博士才会卖这台机器。</summary>
        public const string WinemakerUpgrade = "WINEMAKER";

        public const int MachinePrice = 888;

        public static ModEntry Instance { get; private set; }

        private static ModLog _log;
        public static ModLog Log { get { return _log; } }

        public static void Warn(string message)
        {
            if (_log != null) _log.Warning(message);
        }

        public void Init(ModManifest manifest)
        {
            Instance = this;
            _log = new ModLog(manifest);
            Config.Init(ModId);
            _log.Log(manifest.Name + " v" + manifest.ModVersion + " 已初始化。");
        }

        public void OnEnable()
        {
            ModHook.OnModItemDirectoryInit += OnItemDirectoryInit;
            // Early 比 Late 早：博士一摆货我们就先占位，否则 3x3 的整块空位会被其他货切碎
            ModHook.OnPlaceInventorInventoryItemEarly += OnInventorStock;
            ModHook.OnPlaceInventorInventoryItemLate += OnInventorStock;
            ModHook.OnModConfigApplied += OnConfigApplied;
            ModHook.OnGameLoadedLate += OnGameLoaded;

            RegisterDebugFunction();

            _log.Log("已启用。");
        }

        public void OnDisable()
        {
            ModHook.OnModItemDirectoryInit -= OnItemDirectoryInit;
            ModHook.OnPlaceInventorInventoryItemEarly -= OnInventorStock;
            ModHook.OnPlaceInventorInventoryItemLate -= OnInventorStock;
            ModHook.OnModConfigApplied -= OnConfigApplied;
            ModHook.OnGameLoadedLate -= OnGameLoaded;

            try { ModHelper.RemoveDebugFunctions(ModId); } catch (Exception) { }

            if (_log != null) _log.Log("已停用。");
        }

        // ───────────────────────────────────────────────────── 注册物品

        private static void OnItemDirectoryInit(ModItemDirectory dir)
        {
            try
            {
                dir.Add(BrewerMachine.ItemId, BrewerMachine.Create);
            }
            catch (Exception e)
            {
                if (_log != null) _log.Error("注册酿酒机失败: " + e);
            }
        }

        // ───────────────────────────────────────────────────── 博士库存

        /// <summary>
        /// 每次去博士店里都会触发（他每次到货都重建前台）。
        /// 规则：买过「退休酿酒师」+ 存档里已经没有这台机器 → 上架一台 888。
        /// 不做任何"已买过"标记，让"卖掉/丢掉了就能再买"自然成立。
        /// </summary>
        private static void OnInventorStock(List<GameItem> owned)
        {
            try
            {
                if (!ModHelper.IsNetworkUpgradeUnlocked(WinemakerUpgrade)) return;
                if (owned != null && owned.Any(i => i != null && i.IsID(BrewerMachine.ItemId))) return;

                EmporiumEntry emporium = EmporiumEntry.Instance;
                if (emporium != null
                    && emporium.frontInvinvElement != null
                    && emporium.frontInvinvElement.childItems.Any(i => i != null && i.IsID(BrewerMachine.ItemId)))
                    return;

                GameItem machine = ModHelper.SpawnItem(BrewerMachine.ItemId);
                if (machine == null)
                {
                    if (_log != null) _log.Error("机器未注册，无法上架。");
                    return;
                }

                machine.unitValue = MachinePrice;
                machine.EnableTag(WineTags.IgnoreChildValue);

                if (PlayerStore.Instance == null
                    || !PlayerStore.Instance.TryAddDirectSellingItemToTable(machine, false, false, false, 0))
                {
                    if (_log != null)
                    {
                        int count = emporium != null && emporium.frontInvinvElement != null
                            ? emporium.frontInvinvElement.childItems.Count
                            : -1;
                        _log.Warning("博士前台没有 3x3 整块空位，本次跳过（前台已有 " + count + " 件货）。");
                    }
                }
            }
            catch (Exception e)
            {
                if (_log != null) _log.Error("上架到博士库存失败: " + e);
            }
        }

        // ───────────────────────────────────────────────────── 配置 / 读档

        private static void OnConfigApplied(string modId)
        {
            if (modId == ModId) Config.Apply();
        }

        /// <summary>读档后再刷一次机器名称，避免语言表未就绪时工厂里取到的是兜底串。</summary>
        private static void OnGameLoaded()
        {
            try
            {
                EmporiumEntry emporium = EmporiumEntry.Instance;
                if (emporium == null) return;

                foreach (GameItem item in emporium.GetAllOwnedItems(false, true))
                    if (item != null && item.IsID(BrewerMachine.ItemId))
                        BrewerMachine.RefreshAppearance(item);
            }
            catch (Exception e)
            {
                if (_log != null) _log.Warning("刷新机器名称失败: " + e.Message);
            }
        }

        // ───────────────────────────────────────────────────── 调试入口

        private static void RegisterDebugFunction()
        {
            try
            {
                ModHelper.AddDebugFunction(ModId, ModHelper.DEBUG_CATEGORY_SPAWN, "酿酒便利机（生成）", delegate
                {
                    GameItem machine = ModHelper.SpawnItem(BrewerMachine.ItemId);
                    if (machine == null) return;
                    machine.unitValue = MachinePrice;
                    ModHelper.MoveItemToInventory(machine);
                });
            }
            catch (Exception e)
            {
                if (_log != null) _log.Warning("注册调试按钮失败: " + e.Message);
            }
        }
    }
}
