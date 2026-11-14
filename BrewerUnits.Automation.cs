using System;
using System.Collections.Generic;
using System.Linq;

namespace BrewingConvenience
{
    /// <summary>
    /// 电池供电、芯片升级、酒瓶打印、夜间自动酿酒。
    /// 拆成 partial 文件只是为了让 BrewerUnits.cs 里那套分拣/配比逻辑保持原样，
    /// 两者共享同一份区域引用（BottleBin / FermentBin / …），不会各存一份。
    /// </summary>
    internal sealed partial class BrewerUnits
    {
        // ── 新增的两个槽位，由 BrewerMachine 创建后赋值 ──
        public GameSlotInventory BatterySlot;
        public GameSlotInventory CoreSlot;

        /// <summary>左列那行升级进度文字。</summary>
        public MachineryText UpgradeText;

        /// <summary>空瓶区最多 8 个（8 列 × 4 行，瓶子是 1×4）。</summary>
        private const int MaxBottles = 8;

        /// <summary>
        /// 机器 → 运行上下文。
        /// 夜间自动酿酒发生在窗口关闭、甚至从没打开过的时候，
        /// 只能靠这张表从 GameItem 找回对应的 units。
        /// 工厂每次创建/读档都会重新注册，覆盖旧实例。
        /// </summary>
        private static readonly Dictionary<GameItem, BrewerUnits> Registry =
            new Dictionary<GameItem, BrewerUnits>();

        public static void Register(GameItem machine, BrewerUnits units)
        {
            if (machine == null || units == null) return;
            Registry[machine] = units;
        }

        /// <summary>由 ModEntry 在 OnGoingSleepEarly 里逐个机器调用。</summary>
        public static void RunAutoFor(GameItem machine)
        {
            if (machine == null) return;

            BrewerUnits units;
            if (!Registry.TryGetValue(machine, out units) || units == null)
            {
                ModEntry.Warn("自动酿酒：找不到机器的运行上下文，跳过。");
                return;
            }

            units.RunAuto();
        }

        // ───────────────────────────────────────────────────── 电量

        public GameItem Battery()
        {
            return BatterySlot == null ? null : BatterySlot.childItems.FirstOrDefault(WineTags.IsBattery);
        }

        public int BatteryEnergy()
        {
            return WineTags.BatteryEnergy(Battery());
        }

        public int BatteryMax()
        {
            return WineTags.BatteryMaxEnergy(Battery());
        }

        /// <summary>
        /// 够不够扣一次。除了剩余电量，还要过原版的 bandwidth（单次最大输出）——
        /// 普通电池是 10 kJ，一晚 4 不会卡；但一次性小电池可能只有 2，那就扣不动。
        /// </summary>
        public bool HasEnergy(int kj)
        {
            GameItem battery = Battery();
            if (battery == null) return false;

            int bandwidth = 0;
            try { bandwidth = battery.GetTagReadonly(WineTags.PowerBandwidth).GetInt(); }
            catch (Exception) { }

            if (bandwidth > 0 && kj > bandwidth) return false;
            return WineTags.BatteryEnergy(battery) >= kj;
        }

        public bool ConsumeEnergy(int kj)
        {
            GameItem battery = Battery();
            if (battery == null || !HasEnergy(kj)) return false;

            bool disposable = battery.IsTag(WineTags.PowerDisposable);
            int left = 0;

            battery.ModifyTag(WineTags.PowerEnergy, st =>
            {
                left = Math.Max(0, st.GetInt() - kj);
                st.SetInt(left);
            }, false);

            // 一次性电池见底就该消失。原版是由电力模拟统一处理的，我们手动扣电得自己补。
            if (left <= 0 && disposable)
            {
                try
                {
                    if (battery.parentInventory != null) battery.parentInventory.Expel(battery);
                    battery.Destroy();
                }
                catch (Exception e)
                {
                    ModEntry.Warn("销毁耗尽的一次性电池失败: " + e.Message);
                }
            }

            RefreshUpgradeText();
            return true;
        }

        // ───────────────────────────────────────────────────── 解锁状态

        private int ReadInt(string tag)
        {
            if (Machine == null) return 0;
            try { return Machine.GetTagReadonly(tag).GetInt(); }
            catch (Exception) { return 0; }
        }

        public bool AutoUnlocked { get { return ReadInt(WineTags.AutoUnlockInt) > 0; } }
        public bool PrintUnlocked { get { return ReadInt(WineTags.PrintUnlockInt) > 0; } }
        public int IngotProgress { get { return ReadInt(WineTags.IngotProgressInt); } }
        public int ElecProgress { get { return ReadInt(WineTags.ElecProgressInt); } }

        // ───────────────────────────────────────────────────── 芯片升级

        public bool CanUpgrade(GameItem material)
        {
            if (material == null) return false;
            if (material.IsID(WineTags.MetalIngotId)) return IngotProgress < WineTags.IngotCost;
            if (material.IsID(WineTags.CommonElectronicId)) return ElecProgress < WineTags.ElecCost;
            return false;
        }

        public void DoUpgrade(GameItem material)
        {
            if (!CanUpgrade(material)) return;

            bool ingot = material.IsID(WineTags.MetalIngotId);
            string progressTag = ingot ? WineTags.IngotProgressInt : WineTags.ElecProgressInt;
            string unlockTag = ingot ? WineTags.AutoUnlockInt : WineTags.PrintUnlockInt;
            int cost = ingot ? WineTags.IngotCost : WineTags.ElecCost;

            // 先扣材料，再记进度 —— 顺序反过来的话，Destroy 抛异常会让玩家白拿进度。
            try
            {
                if (material.parentInventory != null) material.parentInventory.Expel(material);
                material.Destroy();
            }
            catch (Exception e)
            {
                ModEntry.Warn("消耗升级材料失败: " + e.Message);
                return;
            }

            int next = ReadInt(progressTag) + 1;
            Machine.ModifyTag(progressTag, st => { st.Enable(); st.SetInt(next); }, false);

            if (next >= cost)
                Machine.ModifyTag(unlockTag, st => { st.Enable(); st.SetInt(1); }, false);

            RefreshUpgradeText();
        }

        /// <summary>
        /// 芯片进槽时挂回调。用的是原版贩卖机核心那套字段
        /// （MaterialDirectory 拖金属锭到 vending_machine_core 上走的就是它们）。
        /// 参数顺序是 (被拖的物品, 自己)。
        /// </summary>
        public void BindCore(GameItem core)
        {
            if (core == null) return;
            core.mayThisTargetItemFunc = (src, self) => CanUpgrade(src);
            core.canThisTargetItemFunc = (src, self) => CanUpgrade(src);
            core.onThisTargetItemFunc = (src, self) => DoUpgrade(src);
        }

        public void RefreshUpgradeText()
        {
            if (UpgradeText == null) return;

            try
            {
                string text;
                if (AutoUnlocked && PrintUnlocked)
                    text = Loc.Get("chip_both", "已全解锁");
                else if (AutoUnlocked)
                    text = Loc.Get("chip_auto", "自动") + " " + ElecProgress + "/" + WineTags.ElecCost;
                else if (PrintUnlocked)
                    text = Loc.Get("chip_print", "打印") + " " + IngotProgress + "/" + WineTags.IngotCost;
                else
                    text = IngotProgress + "/" + WineTags.IngotCost + " " + ElecProgress + "/" + WineTags.ElecCost;

                UpgradeText.SetText(text);
            }
            catch (Exception) { }
        }

        // ───────────────────────────────────────────────────── 打印酒瓶

        public void PrintOne()
        {
            PrintBottles(1);
        }

        public void PrintFill()
        {
            PrintBottles(MaxBottles);
        }

        private void PrintBottles(int want)
        {
            if (!PrintUnlocked)
            {
                SetHint(Loc.Get("why_print_locked", "未解锁打印"));
                return;
            }

            int made = 0;

            for (int i = 0; i < want; i++)
            {
                if (BottleBin.childItems.Count >= MaxBottles) break;
                if (!HasEnergy(WineTags.PrintCostKj)) break;

                GameItem bottle = null;
                try { bottle = DirectoryMaster.Item(WineTags.WineBottleId, true); }
                catch (Exception e) { ModEntry.Warn("创建空酒瓶失败: " + e.Message); }

                if (bottle == null) break;

                GraphUtils.TryAcceptAll(BottleBin, bottle, 1);
                if (bottle.parentInventory == null)
                {
                    // 放不进去就别留着"无主物品"，存档会丢
                    try { bottle.Destroy(); } catch (Exception) { }
                    break;
                }

                ConsumeEnergy(WineTags.PrintCostKj);
                made++;
            }

            if (made > 0) SetHint(Loc.Get("hint_printed", "打印") + " " + made);
            else if (Battery() == null) SetHint(Loc.Get("why_battery", "缺电池"));
            else if (BottleBin.childItems.Count >= MaxBottles) SetHint(Loc.Get("why_bottle_area_full", "瓶区满"));
            else SetHint(Loc.Get("why_power_low", "电量不足"));
        }

        // ───────────────────────────────────────────────────── 夜间自动酿酒

        /// <summary>
        /// 一晚一次，把发酵区填满。固定扣 4 kJ（不管当晚酿几瓶）。
        /// 任何一项必需资源不够就整晚不启动——半途而废比不动更让人困惑。
        /// </summary>
        private void RunAuto()
        {
            if (!AutoUnlocked) return;
            if (Battery() == null) return;
            if (!HasEnergy(WineTags.AutoCostKj)) return;

            // 发酵区没有空位：不扣电、不动作
            if (FermentBin.childItems.Count >= MaxConcurrentBottles) return;

            // 料不齐就别扣电
            if (!Berries().Any()) return;
            if (!BottleBin.childItems.Any(WineTags.IsEmptyBottle)) return;
            if (Water() == null) return;
            if (UseYeast && !YeastBin.childItems.Any(i => WineTags.IsYeast(i) && !WineTags.IsIllegalYeast(i))) return;

            ConsumeEnergy(WineTags.AutoCostKj);

            Sort();
            Brew();
        }

        private void SetHint(string text)
        {
            if (HintText == null) return;
            SetText(HintText, text);
        }
    }
}
