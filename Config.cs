namespace BrewingConvenience
{
    /// <summary>
    /// 全局默认值。目标档位 / 是否用酵母 / 执行类别都是**每台机器**的设置
    /// （存在机器自己的 tag 里，随存档走），这里只提供新机器的初始值。
    /// </summary>
    internal static class Config
    {
        /// <summary>默认目标档位：5 = 琼浆玉液（0 = 难以下咽）。</summary>
        public const int DefaultTargetTier = 5;

        /// <summary>默认必须使用酵母。关掉会少 1 分，但发酵从 2 晚变 4 晚。</summary>
        public const bool DefaultUseYeast = true;

        /// <summary>默认执行类别：false = 优先（够不到目标档就往下退一档）。</summary>
        public const bool DefaultForce = false;
    }
}
