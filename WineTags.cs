using System;

namespace BrewingConvenience
{
    /// <summary>
    /// 原版酿酒系统用到的 tag 名与数值规则。这些字符串是从 Assembly-CSharp.dll 的
    /// WineHelper 常量里逐字抄下来的，改一个字就会静默失效，不要动。
    /// </summary>
    internal static class WineTags
    {
        public const string Berry = "BERRY_TAG";
        public const string BerryCondition = "BERRY_CONDITION_INT";
        public const string Container = "WINE_CONTAINER_TAG";
        public const string State = "WINE_CONTAINER_STATE";
        public const string Score = "WINE_SCORE_INT";
        public const string Remaining = "FERMENTATION_TIME_REMAINING_INT";
        public const string YeastPower = "WINE_YEAST_POWER_INT";
        public const string Yeast = "WINE_YEAST_TAG";

        /// <summary>可复用酵母（酵母生物反应器 wine_yeast_infinite）——发酵时不会被消耗，而是被丢到柜台上。</summary>
        public const string YeastReusable = "WINE_YEAST_REUSABLE_TAG";

        /// <summary>非法酵母（wine_yeast_red）——会把整瓶标记成假酒、分值锁死为 3。</summary>
        public const string IllegalYeast = "WINE_YEAST_ILLEGAL_TAG";

        // ── 每台机器的设置（存机器自己的 tag，随存档走）──
        //
        // ★ 布尔开关不能用 tag 的 enabled 位存：GameItem.IsTag() 的实现是
        //   `return state.IsEnabled()`，Disable() 之后 IsTag 直接变 false，
        //   读取时就分不清"玩家关了"和"从没设置过"，开关永远切不过去。
        //   所以一律用 int 值：0 = 从未设置（回落到 Config 默认值），非 0 才是玩家的选择。
        public const string TargetTierTag  = "BREW_TARGET_TIER_INT";  // 0..5，缺失走默认
        public const string UseYeastIntTag = "BREW_USE_YEAST_INT";    // 0 未设置 / 1 关 / 2 开
        public const string ForceIntTag    = "BREW_FORCE_INT";        // 0 未设置 / 1 优先 / 2 强制
        public const string LiquidContainer = "LIQUID_CONTAINER_TAG";
        public const string IgnoreChildValue = "IGNORE_CHILD_VALUE";

        public const int StateEmpty = 0;
        public const int StateFermenting = 1;
        public const int StateFinished = 2;

        /// <summary>一瓶酒的固定容量（ml）。原版 wine_bottle 的 LIQUID_CONTAINER_CAPACITY = 750000（单位 µL）。</summary>
        public const int BottleMl = 750;

        /// <summary>水质档位阈值，从高到低。与 WaterFeatureHelper.WATER_PURITIES 一致。</summary>
        private static readonly int[] WaterPurities = { 9990, 9900, 9800, 9600, 9000, 3500 };

        /// <summary>水质档位 → 酿酒加分。与 WineHelper.WATER_QUALITY_SCORE 一致。</summary>
        private static readonly int[] WaterScores = { 1, 1, 0, -1, -2, -3 };

        /// <summary>纯度 0..10000 → 档位索引 0..5（纯水/优质/基础/幽灵/锈水/沟渠）。</summary>
        public static int PurityIndex(int purity)
        {
            for (int i = 0; i < WaterPurities.Length; i++)
                if (purity >= WaterPurities[i]) return i;
            return 5;
        }

        /// <summary>某个容器里装的水能给酒加多少分（-3..+1）。</summary>
        public static int WaterScoreOf(GameItem container)
        {
            if (container == null) return 0;
            int idx = PurityIndex(WaterHelper.GetWaterPurity(container));
            if (idx < 0) idx = 0;
            if (idx >= WaterScores.Length) idx = WaterScores.Length - 1;
            return WaterScores[idx];
        }

        /// <summary>
        /// 葡萄的**存储**分值，原样返回，不做任何修正。
        /// 原版取值 {-2,-1,1,2}：-2 发霉、-1 未熟/干枯/过熟、1 正常、2 小果。
        /// 分拣分类必须用这个值 —— 用修正值会把 -1 变成 0，把未熟果误判成正常果。
        /// </summary>
        public static int BerryRawCondition(GameItem berry)
        {
            if (berry == null) return -1;
            return berry.GetTagReadonly(BerryCondition).GetInt();
        }

        /// <summary>
        /// 一颗葡萄参与酿造计算的分值。原版 StartFermentation 对负数做 cond+1，
        /// 存储 {-2,-1,1,2} → 计算 {-1,0,1,2}。
        /// </summary>
        public static int BerryValue(GameItem berry)
        {
            int cond = BerryRawCondition(berry);
            return cond < 0 ? cond + 1 : cond;
        }

        /// <summary>分拣去向：0 = 小果区，1 = 正常果区，2 = 其他果区（次品一律进其他）。</summary>
        public static int BerryBin(GameItem berry)
        {
            int raw = BerryRawCondition(berry);
            if (raw >= 2) return 0;   // 小果
            if (raw >= 0) return 1;   // 正常果（0 / 1）
            return 2;                 // 未熟 / 干枯 / 发霉
        }

        /// <summary>能不能拿来酿酒：只有正常果与小果可以。</summary>
        public static bool IsBrewableBerry(GameItem berry)
        {
            return IsBerry(berry) && BerryRawCondition(berry) >= 0;
        }

        public static int WineState(GameItem item)
        {
            if (item == null) return -1;
            return item.GetTagReadonly(State).GetInt();
        }

        public static bool IsBerry(GameItem item)
        {
            return item != null && item.IsTag(Berry);
        }

        public static bool IsOwned(GameItem item)
        {
            return item != null && GeneralHelper.IsItemOwned(item);
        }

        /// <summary>
        /// 真正的"空酒瓶"。
        /// ★ 必须排除装了液体的瓶子：装了水的啤酒瓶 WINE_CONTAINER_STATE 仍然是 0（空），
        ///   只按状态判断的话它会冒充空瓶被塞进空瓶区，水区永远收不到水，
        ///   开酿时会提示"缺水"却看不出原因。
        /// </summary>
        public static bool IsEmptyBottle(GameItem item)
        {
            return item != null
                && item.IsTag(Container)
                && WineState(item) == StateEmpty
                && !HasLiquid(item);
        }

        /// <summary>
        /// 能不能当水源：装了液体，且不是发酵中/已成品酒瓶。
        /// 成品酒和发酵中的瓶子也带液体，但它们不该被吸进储水区。
        /// </summary>
        public static bool IsWaterSource(GameItem item)
        {
            return HasLiquid(item) && WineState(item) == StateEmpty;
        }

        /// <summary>容器里有没有液体。非液体容器一律 false。</summary>
        public static bool HasLiquid(GameItem item)
        {
            if (item == null || !item.IsTag(LiquidContainer)) return false;
            try { return WaterHelper.GetCurrentCapacityML(item) > 0; }
            catch (Exception) { return false; }
        }

        public static bool IsYeast(GameItem item)
        {
            return item != null && item.IsTag(Yeast);
        }

        /// <summary>分值 → 酒质档位（与原版 ItemFeatureList.CreateWineQualityFeature 同阈值）。</summary>
        public static int Tier(int score)
        {
            if (score >= 6) return 5;
            if (score == 5) return 4;
            if (score >= 3) return 3;
            if (score == 2) return 2;
            if (score == 1) return 1;
            return 0;
        }

        public static string TierKey(int tier)
        {
            switch (tier)
            {
                case 5: return "q_master";
                case 4: return "q_reserve";
                case 3: return "q_production";
                case 2: return "q_homebrew";
                case 1: return "q_bucket";
                default: return "q_undrinkable";
            }
        }

        public static string TierFallback(int tier)
        {
            switch (tier)
            {
                case 5: return "琼浆玉液";
                case 4: return "臻品窖藏";
                case 3: return "量产酒液";
                case 2: return "手工自酿";
                case 1: return "粗桶劣酿";
                default: return "难以下咽";
            }
        }

        public static string TierName(int score)
        {
            return TierNameByTier(Tier(score));
        }

        /// <summary>档位索引 → 本地化名（5 琼浆玉液 … 0 难以下咽）。</summary>
        public static string TierNameByTier(int tier)
        {
            return Loc.Get(TierKey(tier), TierFallback(tier));
        }

        public static bool IsIllegalYeast(GameItem item)
        {
            return item != null && item.IsTag(IllegalYeast);
        }
    }
}
