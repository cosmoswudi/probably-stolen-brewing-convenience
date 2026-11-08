using System;

namespace BrewingConvenience
{
    /// <summary>
    /// 模组配置（写在 ModConfig 的独立配置文件里，不随存档）。
    /// 目前只有一个开关：目标品质。默认琼浆玉液(6 分)，打开则做臻品窖藏(恰好 5 分)。
    /// </summary>
    internal static class Config
    {
        /// <summary>目标 wineScore：6 = 琼浆玉液，5 = 臻品窖藏。</summary>
        public static int TargetScore = 6;

        private static ConfigEntry<bool> _reserve;

        public static void Init(string modId)
        {
            try
            {
                ModConfig cfg = ModHelper.GetConfig(modId);
                if (cfg == null) return;

                _reserve = cfg.Define(
                    "target_reserve",
                    false,
                    "General",
                    () => Loc.Get("cfg_target", "目标是臻品窖藏(5分)"),
                    () => Loc.Get("cfg_target_tip", "关闭 = 酿琼浆玉液(6分)；打开 = 酿臻品窖藏(恰好5分)"));

                Apply();
            }
            catch (Exception e)
            {
                ModEntry.Warn("配置初始化失败: " + e.Message);
            }
        }

        public static void Apply()
        {
            TargetScore = (_reserve != null && _reserve.Value) ? 5 : 6;
        }
    }
}
