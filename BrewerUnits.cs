using System;
using System.Collections.Generic;
using System.Linq;

namespace BrewingConvenience
{
    /// <summary>
    /// 一台机器实例的运行时上下文：八个区域 + 三个状态文本。
    /// 每次工厂函数执行都会新建一份，UI 回调通过闭包持有它 —— 不使用任何静态状态，
    /// 这样读档重建时不会串台，多台机器也能各自独立工作。
    /// </summary>
    internal sealed class BrewerUnits
    {
        public GameItem Machine;
        public GameGridInventory GrapeInput;
        public GameGridInventory NormalBin;
        public GameGridInventory SmallBin;
        public GameGridInventory OtherBin;
        public GameGridInventory BottleBin;
        public GameGridInventory YeastBin;
        public GameGridInventory WaterBin;
        public GameGridInventory FermentBin;

        public MachineryText BestText;
        public MachineryText HintText;

        /// <summary>发酵/成品面板本体 —— 发酵进度直接写在它的标题上，不占状态行高度。</summary>
        public MachineryPanel FermentPanel;

        /// <summary>一瓶最多同时开酿的瓶数（发酵区 8x4 / 瓶子 1x4 = 8 个位置）。</summary>
        private const int MaxConcurrentBottles = 8;

        /// <summary>
        /// 分拣进行中。三个输出区平时拒绝玩家手动放入葡萄，
        /// 只有机器自己在分拣时（此标志为 true）才允许写入。
        /// </summary>
        public bool Sorting;

        private string _lastBest;
        private string _lastHint;
        private string _lastFermentLabel;

        /// <summary>
        /// 批量搬动物品时挂起 UI 刷新。区域回调是「每次放入/取出都触发」的，
        /// 分拣一批 16 颗葡萄会触发几十次刷新，每次都让面板重量宽度 → 明显卡顿。
        /// </summary>
        private bool _suspendRefresh;

        /// <summary>把正常区/小果区里的次品（未熟/干枯/发霉）挪到其他区。</summary>
        private void SweepBins()
        {
            Sweep(NormalBin);
            Sweep(SmallBin);
        }

        private void Sweep(GameGridInventory bin)
        {
            if (bin == null) return;
            foreach (GameItem item in bin.childItems.ToList())
            {
                if (item == null) continue;
                if (WineTags.BerryRawCondition(item) >= 0) continue;
                if (!GraphUtils.CanAccept(OtherBin, item)) continue;

                bin.Expel(item);
                GraphUtils.TryAcceptAll(OtherBin, item, 1);
                if (item.parentInventory == null) bin.TryAcceptAll(item);
            }
        }

        // ─────────────────────────────────────────────────── 投放区分流

        private bool _routing;

        /// <summary>
        /// 「不开窗直接把物品扔到机器上」时，原版只会把物品往第一个区域塞，
        /// 而第一个区域正好是葡萄投放区。所以让投放区当总入口：接收所有本机器能吃的物品，
        /// 落地后立刻把非葡萄的物品转到对应区域。玩家看到的就是"丢进投放口，机器自动归位"。
        /// </summary>
        public void RouteFromIntake()
        {
            if (_routing) return;
            _routing = true;

            try
            {
                foreach (GameItem item in GrapeInput.childItems.ToList())
                {
                    if (item == null || WineTags.IsBerry(item)) continue;

                    GameGridInventory target = null;
                    if (WineTags.IsYeast(item)) target = YeastBin;
                    else if (WineTags.IsEmptyBottle(item)) target = BottleBin;
                    else if (item.IsTag(WineTags.LiquidContainer)) target = WaterBin;
                    if (target == null) continue;

                    GrapeInput.Expel(item);
                    GraphUtils.TryAcceptAll(target, item, 1);

                    // 目标区放不下就交还给玩家，绝不让它卡在投放区
                    if (item.parentInventory == null) GraphUtils.EmporiumTryAdd(item);
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("投放区分流失败: " + e.Message);
            }
            finally
            {
                _routing = false;
            }
        }

        // ─────────────────────────────────────────────────── 直接投放到机器上

        /// <summary>这台机器能从"扔到机器上"的动作里接收哪些物品。</summary>
        public bool CanTake(GameItem item)
        {
            if (item == null) return false;
            if (WineTags.IsBerry(item)) return true;
            if (WineTags.IsYeast(item)) return true;
            if (WineTags.IsEmptyBottle(item)) return true;
            if (item.IsTag(WineTags.LiquidContainer))
                return WaterBin != null && WaterBin.childItems.Count == 0;
            return false;
        }

        /// <summary>把扔上来的物品放进对应区域。成功返回 true。</summary>
        public bool Take(GameItem item)
        {
            try
            {
                GameGridInventory target = null;

                if (WineTags.IsBerry(item)) target = GrapeInput;
                else if (WineTags.IsYeast(item)) target = YeastBin;
                else if (WineTags.IsEmptyBottle(item)) target = BottleBin;
                else if (item != null && item.IsTag(WineTags.LiquidContainer)) target = WaterBin;

                if (target != null)
                {
                    GraphUtils.TryAcceptAll(target, item, 1);
                    if (item.parentInventory != null)
                    {
                        Refresh();
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                ModEntry.Warn("直接投放分发失败: " + e.Message);
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────── 分拣

        public void Sort()
        {
            int moved = 0;
            int full = 0;

            Sorting = true;
            _suspendRefresh = true;
            try
            {
                foreach (GameItem berry in GrapeInput.childItems.ToList())
                {
                    if (berry == null || !WineTags.IsBerry(berry)) continue;

                    int bin = WineTags.BerryBin(berry);
                    GameGridInventory target = bin == 0 ? SmallBin : (bin == 1 ? NormalBin : OtherBin);

                    if (!GraphUtils.CanAccept(target, berry))
                    {
                        full++;
                        continue;
                    }

                    GrapeInput.Expel(berry);
                    GraphUtils.TryAcceptAll(target, berry, 1);
                    if (berry.parentInventory == null)
                    {
                        // 放不进去就把葡萄退回投放区，绝不留在"无主"状态（无主物品会在存档时丢失）
                        GrapeInput.TryAcceptAll(berry);
                    }
                    else
                    {
                        moved++;
                    }
                }

                // 顺带清理：正常区/小果区里如果混进了次品（旧存档遗留），一并挪到其他区
                SweepBins();
            }
            finally
            {
                Sorting = false;
                _suspendRefresh = false;
            }

            if (HintText != null)
            {
                string label = Loc.Get("hint_sorted", "已分拣");
                string value = moved + (full > 0 ? " · " + Loc.Get("hint_full", "满了") + full : "");
                if (moved == 0 && full == 0) value = Loc.Get("hint_nothing", "没有可分的葡萄");
                SetStatus(HintText, label, value);
            }

            Refresh();
        }

        // ─────────────────────────────────────────────────────────── 酿造

        public void Brew()
        {
            int target = Config.TargetScore;
            int brewed = 0;
            string reason = null;

            _suspendRefresh = true;
            try
            {
                for (int i = 0; i < MaxConcurrentBottles; i++)
                {
                    string why;
                    if (!BrewOne(target, out why))
                    {
                        reason = why;
                        break;
                    }
                    brewed++;
                }
            }
            finally
            {
                _suspendRefresh = false;
            }

            if (HintText != null)
            {
                string label = Loc.Get("hint_brew", "已开酿");
                string value = brewed.ToString();
                if (reason != null) value += " · " + reason;
                if (brewed == 0 && reason == null) value = Loc.Get("hint_nothing_brew", "材料不足");
                SetStatus(HintText, label, value);
            }

            Refresh();
        }

        /// <summary>配一瓶：挑两颗分值达标的葡萄 + 一个酵母 + 灌 750ml 水 → 起酵 → 进发酵区。</summary>
        private bool BrewOne(int target, out string reason)
        {
            reason = null;

            GameItem water = Water();
            if (water == null)
            {
                reason = Loc.Get("why_water_container", "缺水容器");
                return false;
            }
            if (WaterHelper.GetCurrentCapacityML(water) < WineTags.BottleMl)
            {
                reason = Loc.Get("why_water", "水量不足 750ml");
                return false;
            }

            // wineScore = 两果分值之和 + 1(果子≥2颗) + 1(有酵母) + 水质分
            int waterScore = WineTags.WaterScoreOf(water);
            int need = target - 2 - waterScore;
            if (need < 0) need = 0;
            if (need > 4)
            {
                reason = Loc.Get("why_bad_water", "水质太差");
                return false;
            }

            GameItem bottle = BottleBin.childItems.FirstOrDefault(WineTags.IsEmptyBottle);
            if (bottle == null)
            {
                reason = Loc.Get("why_bottle", "缺空酒瓶");
                return false;
            }

            if (FermentBin.childItems.Count >= MaxConcurrentBottles)
            {
                reason = Loc.Get("why_ferment_full", "发酵区已满");
                return false;
            }

            GameItem yeast = YeastBin.childItems.FirstOrDefault(WineTags.IsYeast);
            if (yeast == null)
            {
                reason = Loc.Get("why_yeast", "缺酵母");
                return false;
            }

            // 臻品窖藏要求分值"恰好"5，多一分就跳到量产/琼浆，所以必须精确匹配
            List<BerryPick> pair = FindPair(Berries(), need, exact: target == 5);
            if (pair == null)
            {
                reason = Loc.Get("why_berry", "没有合适的两颗葡萄");
                return false;
            }

            // ── 从各区取出 ──
            bool reusableYeast = yeast.IsTag(WineTags.YeastReusable);

            BottleBin.Expel(bottle);
            pair[0].Bin.Expel(pair[0].Item);
            pair[1].Bin.Expel(pair[1].Item);
            YeastBin.Expel(yeast);

            // ── 装进酒瓶 ──
            bool ok = PutInBottle(bottle, pair[0].Item)
                   && PutInBottle(bottle, pair[1].Item)
                   && PutInBottle(bottle, yeast);

            if (!ok)
            {
                ReturnHome(pair[0]);
                ReturnHome(pair[1]);
                if (yeast.parentInventory == null) YeastBin.TryAcceptAll(yeast);
                if (bottle.parentInventory == null) BottleBin.TryAcceptAll(bottle);
                reason = Loc.Get("why_put_fail", "装瓶失败");
                return false;
            }

            // ── 灌水 + 起酵（用 StartFermentation 而不是 TryStartFermentation，后者会弹确认框）──
            WaterHelper.TransferLiquid(water, bottle);
            WineHelper.StartFermentation(bottle);

            // 生物反应器不会被消耗，原版会把它丢到柜台上 —— 立刻捞回酵母区，下一瓶接着用
            if (reusableYeast) RecoverYeast(yeast);

            bottle.Validate();

            // ── 进发酵区。发酵推进交给酒瓶自己的 onCycleEndSlotItemFunc（原版每晚结算）──
            GraphUtils.TryAcceptAll(FermentBin, bottle, 1);
            if (bottle.parentInventory == null)
            {
                // 兜底：发酵区意外放不下时把酒瓶丢到店面上，避免工序做到一半物品消失
                GraphUtils.EmporiumTryAdd(bottle);
                ModEntry.Warn("发酵区放不下酒瓶，已放回店面。");
            }
            return true;
        }

        private static bool PutInBottle(GameItem bottle, GameItem item)
        {
            GraphUtils.TryAcceptAll(bottle, item, 1);
            return item.parentInventory != null;
        }

        /// <summary>
        /// 酵母生物反应器是可复用酵母：原版 StartFermentation 对它做的是
        /// 「先从瓶子里取出 → EmporiumTryAdd」，也就是扔到柜台上而不是消耗掉。
        /// 机器要连酿，就必须在起酵后立刻把它捞回酵母区。
        /// </summary>
        private void RecoverYeast(GameItem yeast)
        {
            try
            {
                if (yeast == null) return;

                GameInventory parent = yeast.parentInventory;
                if (parent != null) parent.Expel(yeast);

                if (yeast.parentInventory == null) YeastBin.TryAcceptAll(yeast);
                if (yeast.parentInventory == null) GraphUtils.EmporiumTryAdd(yeast);

                // 最坏情况：酵母区满了、店面也放不下，它留在柜台上，玩家手动拿回来即可
            }
            catch (Exception e)
            {
                ModEntry.Warn("回收酵母生物反应器失败: " + e.Message);
            }
        }

        private static void ReturnHome(BerryPick pick)
        {
            if (pick.Item != null && pick.Item.parentInventory == null)
                pick.Bin.TryAcceptAll(pick.Item);
        }

        /// <summary>
        /// 在候选葡萄里找一对：exact=true 要求和恰好等于 need，否则要求和 ≥ need。
        /// 同等条件下取"和最小、且最大那颗最小"的组合，把好果子留给下一瓶。
        /// </summary>
        private static List<BerryPick> FindPair(List<BerryPick> all, int need, bool exact)
        {
            List<BerryPick> best = null;
            int bestSum = int.MaxValue;
            int bestMax = int.MaxValue;

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    int sum = all[i].Value + all[j].Value;
                    if (exact ? sum != need : sum < need) continue;

                    int max = Math.Max(all[i].Value, all[j].Value);
                    if (sum < bestSum || (sum == bestSum && max < bestMax))
                    {
                        bestSum = sum;
                        bestMax = max;
                        best = new List<BerryPick> { all[i], all[j] };
                    }
                }
            }

            return best;
        }

        // ─────────────────────────────────────────────────────────── 状态刷新

        /// <summary>
        /// 刷新面板文本。只在「窗口打开 / 点按钮 / 区域物品增删 / 每日结算」时调用，
        /// 绝不放进每秒 tick —— 状态文本一变长变短，整行面板都要重新量宽度并重排，
        /// 每秒来一次会把主线程压住（实测能触发 60 秒的掉帧冻结）。
        /// 另外这里做了变更检测：文本没变就完全不碰 UI。
        /// </summary>
        public void Refresh(bool force = false)
        {
            if (_suspendRefresh) return;

            try
            {
                if (Machine == null || Machine.contentWindow == null) return;

                int fermenting = 0;
                int finished = 0;
                if (FermentBin != null)
                {
                    foreach (GameItem item in FermentBin.childItems)
                    {
                        int state = WineTags.WineState(item);
                        if (state == WineTags.StateFermenting) fermenting++;
                        else if (state == WineTags.StateFinished) finished++;
                    }
                }

                // 发酵进度写进面板标题（"发酵/成品 2+1"），这样控制区只剩两行文字，第二排才压得下来
                if (FermentPanel != null)
                {
                    string label = Loc.Get("panel_ferment", "发酵 / 成品");
                    if (fermenting > 0 || finished > 0)
                        label += " " + fermenting + (finished > 0 ? "+" + finished : "");

                    if (force || label != _lastFermentLabel)
                    {
                        _lastFermentLabel = label;
                        try { FermentPanel.SetLabel(label); } catch (Exception) { }
                    }
                }

                if (BestText != null)
                {
                    string value = BestQualityName();
                    if (force || value != _lastBest)
                    {
                        _lastBest = value;
                        SetStatus(BestText, Loc.Get("status_best", "当前可酿最优"), value);
                    }
                }
            }
            catch (Exception)
            {
                // UI 还没构建完成时可能抛异常，静默跳过
            }
        }

        private static void SetStatus(MachineryText text, string label, string value)
        {
            try { text.SetStatus(label, value); } catch { }
        }

        /// <summary>按"当前水质 + 现有葡萄实际分值 + 有没有酵母"精算能酿到的最高品质。</summary>
        private string BestQualityName()
        {
            GameItem water = Water();
            if (water == null) return Loc.Get("q_no_water_container", "缺水容器");
            if (WaterHelper.GetCurrentCapacityML(water) < WineTags.BottleMl)
                return Loc.Get("q_water_low", "水量不足");

            int waterScore = WineTags.WaterScoreOf(water);

            List<int> values = Berries().Select(b => b.Value).OrderByDescending(v => v).ToList();
            int count = Math.Min(2, values.Count);
            if (count < 2) return Loc.Get("q_need_berry", "需要两颗葡萄");

            int sum = 0;
            for (int i = 0; i < count; i++) sum += values[i];

            bool hasYeast = YeastBin != null && YeastBin.childItems.Any(WineTags.IsYeast);
            int score = sum + (count >= 2 ? 1 : 0) + (hasYeast ? 1 : 0) + waterScore;

            return WineTags.TierName(score);
        }

        // ─────────────────────────────────────────────────────────── 小工具

        private GameItem Water()
        {
            if (WaterBin == null) return null;
            return WaterBin.childItems.FirstOrDefault(i => i != null && i.IsTag(WineTags.LiquidContainer));
        }

        /// <summary>可用于酿酒的葡萄：正常果区 + 小果区（其他果区不参与）。</summary>
        private List<BerryPick> Berries()
        {
            var result = new List<BerryPick>();
            Collect(NormalBin, result);
            Collect(SmallBin, result);
            return result;
        }

        private static void Collect(GameGridInventory bin, List<BerryPick> sink)
        {
            if (bin == null) return;
            foreach (GameItem item in bin.childItems)
                if (WineTags.IsBrewableBerry(item))
                    sink.Add(new BerryPick { Item = item, Bin = bin, Value = WineTags.BerryValue(item) });
        }

        private sealed class BerryPick
        {
            public GameItem Item;
            public GameGridInventory Bin;
            public int Value;
        }
    }
}
