namespace BrewingConvenience
{
    /// <summary>
    /// 本地化取值。模组语言表在 Init/OnEnable 阶段可能还没加载完，
    /// 所以一律带一份硬编码兜底，取不到就用兜底串（不会出现裸 key）。
    /// </summary>
    internal static class Loc
    {
        public static string Get(string key, string fallback)
        {
            try
            {
                string value = ModHelper.GetLocalized(ModEntry.ModId, key);
                if (!string.IsNullOrEmpty(value) && value != key) return value;
            }
            catch
            {
                // 语言表未就绪时忽略，用兜底串
            }
            return fallback;
        }
    }
}
