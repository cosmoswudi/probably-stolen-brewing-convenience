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

        public MachineryText HintText;

        /// <summary>发酵/成品面板本体 —— 发酵进度直接写在它的标题上，不占状态行高度。</summary>
        public MachineryPanel FermentPanel;

        // 控制区的三个控件，文字要随状态变，所以留着引用
        public MachineryButton TargetButton;
        public MachineryButton YeastButton;
        public MachineryButton ForceButton;
        public MachineryButton PriorityButton;

        /// <summary>一瓶最多同时开酿的瓶数（发酵区 8x4 / 瓶子 1x4 = 8 个位置）。</summary>
        private const int MaxConcurrentBottles = 8;

        /// <summary>
        /// 分拣进行中。三个输出区平时拒绝玩家手动放入葡萄，
        /// 只有机器自己在分拣时（此标志为 true）才允许写入。
        /// </summary>
        public bool Sorting;

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

        /// <summary>
        /// 清空操作提示。玩家一往投放区丢新葡萄就调用 ——
        /// 否则上次那句"1瓶琼浆 缺果"会一直挂在面板上，看着像当前状态。
        /// </summary>
        public void ClearHint()
        {
            try
            {
                if (HintText == null) return;
                if (HintText.GetText() == "—") return;
                HintText.SetText("—");
            }
            catch (Exception) { }
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
                string value = Loc.Get("hint_sorted", "分拣") + " " + moved;
                if (full > 0) value += " " + Loc.Get("hint_full", "满") + full;
                if (moved == 0 && full == 0) value = Loc.Get("hint_nothing", "无可分葡萄");
                SetText(HintText, Clip(value, 14));
            }

            Refresh();
        }

        // ─────────────────────────────────── 目标档位 / 酵母 / 执行类别

        /// <summary>最高档位 = 琼浆玉液；最低 = 难以下咽。</summary>
        public const int MaxTier = 5;

        /// <summary>「优先」模式向下退档的底限：量产酒液。再往下就不酿了，免得把葡萄烧成垃圾酒。</summary>
        private const int MinFallbackTier = 3;

        /// <summary>目标档位（5..0）。老机器没有这个 tag 时读全局默认值。</summary>
        public int TargetTier
        {
            get
            {
                if (Machine != null && Machine.IsTag(WineTags.TargetTierTag))
                    return ClampTier(Machine.GetTagReadonly(WineTags.TargetTierTag).GetInt());
                return Config.DefaultTargetTier;
            }
        }

        /// <summary>是否必须使用酵母（关掉少 1 分，发酵 2 晚变 4 晚）。</summary>
        public bool UseYeast
        {
            get { return ReadThreeState(WineTags.UseYeastIntTag, Config.DefaultUseYeast ? 2 : 1) == 2; }
        }

        /// <summary>true = 强制（必须落在目标档），false = 优先（够不到就往下退）。</summary>
        public bool ForceMode
        {
            get { return ReadThreeState(WineTags.ForceIntTag, Config.DefaultForce ? 2 : 1) == 2; }
        }

        /// <summary>
        /// 读三态 int tag：0 / 不存在 = 从未设置，返回 fallback。
        /// 不用 IsTag 判存在性 —— 它等价于 IsEnabled()，被 Disable 过就查不到。
        /// </summary>
        private int ReadThreeState(string tag, int fallback)
        {
            if (Machine == null) return fallback;
            try
            {
                int v = Machine.GetTagReadonly(tag).GetInt();
                return v == 0 ? fallback : v;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static int ClampTier(int tier)
        {
            if (tier < 0) return 0;
            if (tier > MaxTier) return MaxTier;
            return tier;
        }

        /// <summary>点一下降一档，到底后回到最高档。</summary>
        public void CycleTier()
        {
            int next = TargetTier - 1;
            if (next < 0) next = MaxTier;
            Machine.ModifyTag(WineTags.TargetTierTag, st => { st.Enable(); st.SetInt(next); }, false);
            RefreshButtons();
        }

        public void ToggleYeast()
        {
            int next = UseYeast ? 1 : 2;
            Machine.ModifyTag(WineTags.UseYeastIntTag, st => { st.Enable(); st.SetInt(next); }, false);
            RefreshButtons();
        }

        public void SetForce(bool force)
        {
            int next = force ? 2 : 1;
            Machine.ModifyTag(WineTags.ForceIntTag, st => { st.Enable(); st.SetInt(next); }, false);
            RefreshButtons();
        }

        /// <summary>把三个控件的文字刷成当前状态（方括号表示当前选中）。</summary>
        public void RefreshButtons()
        {
            try
            {
                string forceName = Loc.Get("ui_force", "强制");
                string priorityName = Loc.Get("ui_priority", "优先");

                if (TargetButton != null)
                    TargetButton.SetText(Loc.Get("ui_target", "目标") + ":" + WineTags.TierNameByTier(TargetTier));
                if (YeastButton != null)
                    YeastButton.SetText(UseYeast ? Loc.Get("ui_yeast_on", "酵母 开") : Loc.Get("ui_yeast_off", "酵母 关"));
                if (ForceButton != null)
                    ForceButton.SetText(ForceMode ? "[" + forceName + "]" : forceName);
                if (PriorityButton != null)
                    PriorityButton.SetText(ForceMode ? priorityName : "[" + priorityName + "]");
            }
            catch (Exception)
            {
                // 控件还没建好时忽略
            }
        }

        // ─────────────────────────────────────────────────────────── 酿造

        public void Brew()
        {
            int targetTier = TargetTier;
            bool force = ForceMode;
            bool useYeast = UseYeast;

            int brewed = 0;
            int madeTier = -1;
            string reason = null;

            _suspendRefresh = true;
            try
            {
                for (int i = 0; i < MaxConcurrentBottles; i++)
                {
                    string why;
                    int tier;
                    if (!BrewOne(targetTier, force, useYeast, out why, out tier))
                    {
                        reason = why;
                        break;
                    }
                    brewed++;
                    madeTier = tier;
                }
            }
            finally
            {
                _suspendRefresh = false;
            }

            if (HintText != null)
            {
                // 只报"结果"或"卡住的原因"，不报瓶数 —— 瓶数玩家看发酵区就知道，
                // 而这一行宽度有限，混在一起会变成 "2量产 缺空…" 这种谁也读不懂的碎片。
                string value;
                if (reason != null)
                {
                    value = reason;
                }
                else if (brewed > 0)
                {
                    value = ShortTier(madeTier);
                }
                else
                {
                    value = Loc.Get("hint_nothing_brew", "缺料");
                }
                SetText(HintText, value);
            }

            RefreshButtons();
            Refresh();
        }

        /// <summary>
        /// 配一瓶：挑两颗能落到目标档的葡萄（可选酵母）+ 灌 750ml 水 → 起酵 → 进发酵区。
        /// force=true 时够不到目标档就不酿；false 时向下退档，底限是量产酒液。
        /// </summary>
        private bool BrewOne(int targetTier, bool force, bool useYeast, out string reason, out int madeTier)
        {
            reason = null;
            madeTier = -1;

            GameItem water = Water();
            if (water == null)
            {
                reason = Loc.Get("why_water_container", "缺水");
                return false;
            }
            if (WaterHelper.GetCurrentCapacityML(water) < WineTags.BottleMl)
            {
                reason = Loc.Get("why_water", "水量不足");
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
                reason = Loc.Get("why_ferment_full", "发酵满");
                return false;
            }

            GameItem yeast = null;
            if (useYeast)
            {
                // 非法酵母会把整瓶变成假酒（分值被强制成 3），直接跳过它，用下一颗合法酵母
                yeast = YeastBin.childItems.FirstOrDefault(i => WineTags.IsYeast(i) && !WineTags.IsIllegalYeast(i));
                if (yeast == null)
                {
                    reason = Loc.Get("why_yeast", "缺酵母");
                    return false;
                }
            }

            int waterScore = WineTags.WaterScoreOf(water);
            int baseBonus = 1 + (useYeast ? 1 : 0);   // 果子数加成 + 酵母加成

            // 一次取齐候选，后面反复比对不用重复遍历三个果区
            List<BerryPick> candidates = Berries();
            List<BerryPick> pair = FindPairForTier(candidates, baseBonus, waterScore, targetTier);
            madeTier = targetTier;

            if (pair == null)
            {
                if (force)
                {
                    // 分清两种情况：果区里根本没两颗果子 / 有果子但分值凑不到这一档。
                    // 之前统一报"够不到"，玩家会以为机器坏了。
                    reason = candidates.Count < 2
                        ? Loc.Get("why_no_berry", "缺果")
                        : Loc.Get("why_tier_locked", "果不够");
                    return false;
                }

                // 目标档本身低于底限时（例如玩家主动选"手工自酿"），底限跟着下调，
                // 否则优先模式会连目标档都够不到就直接罢工。
                int floor = Math.Min(MinFallbackTier, targetTier);
                for (int t = targetTier - 1; t >= floor; t--)
                {
                    pair = FindPairForTier(candidates, baseBonus, waterScore, t);
                    if (pair != null)
                    {
                        madeTier = t;
                        break;
                    }
                }

                if (pair == null)
                {
                    reason = candidates.Count < 2
                        ? Loc.Get("why_no_berry", "缺果")
                        : Loc.Get("why_berry", "无合适果");
                    return false;
                }
            }

            // ── 从各区取出 ──
            bool reusableYeast = yeast != null && yeast.IsTag(WineTags.YeastReusable);

            BottleBin.Expel(bottle);
            pair[0].Bin.Expel(pair[0].Item);
            pair[1].Bin.Expel(pair[1].Item);
            if (yeast != null) YeastBin.Expel(yeast);

            // ── 装进酒瓶 ──
            bool ok = PutInBottle(bottle, pair[0].Item)
                   && PutInBottle(bottle, pair[1].Item)
                   && (yeast == null || PutInBottle(bottle, yeast));

            if (!ok)
            {
                ReturnHome(pair[0]);
                ReturnHome(pair[1]);
                if (yeast != null && yeast.parentInventory == null) YeastBin.TryAcceptAll(yeast);
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
        /// 找一对葡萄，使 wineScore 正好落在指定档位里。
        /// 有多组可选时取分值最低的那组，把好果子留给下一瓶。
        /// </summary>
        private static List<BerryPick> FindPairForTier(List<BerryPick> all, int baseBonus, int waterScore, int tier)
        {
            List<BerryPick> best = null;
            int bestScore = int.MaxValue;

            for (int i = 0; i < all.Count; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    int score = all[i].Value + all[j].Value + baseBonus + waterScore;
                    if (WineTags.Tier(score) != tier) continue;

                    if (score < bestScore)
                    {
                        bestScore = score;
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

            }
            catch (Exception)
            {
                // UI 还没构建完成时可能抛异常，静默跳过
            }
        }

        /// <summary>档位短名（只有两个字，塞得进状态行）。</summary>
        private static string ShortTier(int tier)
        {
            switch (tier)
            {
                case 5: return Loc.Get("tier5_s", "琼浆");
                case 4: return Loc.Get("tier4_s", "臻品");
                case 3: return Loc.Get("tier3_s", "量产");
                case 2: return Loc.Get("tier2_s", "自酿");
                case 1: return Loc.Get("tier1_s", "粗桶");
                default: return Loc.Get("tier0_s", "劣酿");
            }
        }

        /// <summary>
        /// 按"显示宽度"截断（中文算 2、ASCII 算 1，上限 weight）。
        /// 状态行是自然宽度的文本，字一多整块控制面板就会变宽、把窗口撑大，
        /// 所以宁可截断也不让它无限长。中英文用同一套权重，两边都不会超宽。
        /// </summary>
        private static string Clip(string text, int weight)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int used = 0;
            int cut = 0;
            for (; cut < text.Length; cut++)
            {
                used += text[cut] > 0x7F ? 2 : 1;
                if (used > weight) break;
            }

            if (cut >= text.Length) return text;
            return text.Substring(0, Math.Max(1, cut - 1)) + "…";
        }

        private static void SetText(MachineryText text, string value)
        {
            try { text.SetText(value); } catch { }
        }

        // ─────────────────────────────────────────────────────────── 小工具

        private GameItem Water()
        {
            if (WaterBin == null) return null;
            return WaterBin.childItems.FirstOrDefault(i => i != null && i.IsTag(WineTags.LiquidContainer));
        }

        /// <summary>可用于酿酒的葡萄：正常果区 + 小果区（其他果区不参与）。</summary>
        /// <summary>
        /// 参与酿酒的全部葡萄：正常果 / 小果 / 其他果。
        /// 其他果区装的是次品（未熟/干枯/发霉），它们的分值是 0 或负，只有目标档位很低时才会被选中 ——
        /// 所以放开不会污染高档酒，但让"手工自酿""粗桶劣酿""难以下咽"这几档真正可达。
        /// </summary>
        private List<BerryPick> Berries()
        {
            var result = new List<BerryPick>();
            Collect(NormalBin, result);
            Collect(SmallBin, result);
            Collect(OtherBin, result);
            return result;
        }

        private static void Collect(GameGridInventory bin, List<BerryPick> sink)
        {
            if (bin == null) return;
            foreach (GameItem item in bin.childItems)
                if (WineTags.IsBerry(item))
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
