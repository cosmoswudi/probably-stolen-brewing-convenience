using System;
using System.Collections.Generic;
using TMPro;

namespace BrewingConvenience
{
    /// <summary>
    /// 酿酒便利机本体。
    ///
    /// ★★★ 重要：区域创建顺序已经冻结 ★★★
    /// 游戏的存档只记录「子物品所属的背包节点序号」，不记录是哪个网格。
    /// 所以 Create() 每次都必须以完全相同的顺序创建这 8 个 GameGridInventory，
    /// 一旦发布后增删/重排区域，老存档里的葡萄和酒瓶就会落到错的格子里。
    /// </summary>
    internal static class BrewerMachine
    {
        /// <summary>物品 identifier。存档靠它重建物品，发布后不可更改。</summary>
        public const string ItemId = "brewing_convenience_machine";

        /// <summary>贴图键：Mods/brewing_convenience/Textures/Items/brewer.png（48x48 → 3x3 占格）。</summary>
        public const string SpriteKey = "brewer";

        public static GameItem Create()
        {
            var units = new BrewerUnits();

            // ── 8 个区域，顺序即存档序号 ─────────────────────────────────
            units.GrapeInput = new GameGridInventory(8, 8); // 1 葡萄投放区
            units.NormalBin = new GameGridInventory(8, 8);  // 2 正常果
            units.SmallBin = new GameGridInventory(8, 8);   // 3 小果
            units.OtherBin = new GameGridInventory(8, 8);   // 4 其他果
            units.BottleBin = new GameGridInventory(8, 4);  // 5 空酒瓶（横版 8 列 × 4 行 = 8 瓶）
            units.YeastBin = new GameGridInventory(3, 3);   // 6 酵母
            units.WaterBin = new GameGridInventory(4, 4);   // 7 储水（只放一个容器）
            units.FermentBin = new GameGridInventory(8, 4); // 8 发酵中 / 成品（横版 8 瓶）

            // ── 各区域只收该收的东西 ───────────────────────────────────
            // 葡萄只能拖进「投放区」；正常/小/其他三个输出区平时一律拒绝手动放入，
            // 只有点「分拣」时（units.Sorting = true）机器自己才写得进去。
            // 投放区是总入口：葡萄、酵母、空瓶、装水容器都收，非葡萄落地后立刻分流到对应区域
            units.GrapeInput.mayInventoryAddItemFunc = (item, inv) =>
            {
                if (item == null || !WineTags.IsOwned(item)) return false;
                if (WineTags.IsBerry(item)) return true;

                // 非葡萄的物品只是"路过"投放区：目标区域放不下就直接拒收，
                // 否则它会被迫留在投放区里占着葡萄的位置。
                if (WineTags.IsYeast(item)) return GraphUtils.CanAccept(units.YeastBin, item);
                if (WineTags.IsEmptyBottle(item)) return GraphUtils.CanAccept(units.BottleBin, item);
                if (item.IsTag(WineTags.LiquidContainer))
                    return units.WaterBin.childItems.Count == 0 && GraphUtils.CanAccept(units.WaterBin, item);

                return false;
            };
            units.NormalBin.mayInventoryAddItemFunc = (item, inv) => units.Sorting && WineTags.IsBerry(item) && WineTags.IsOwned(item);
            units.SmallBin.mayInventoryAddItemFunc = (item, inv) => units.Sorting && WineTags.IsBerry(item) && WineTags.IsOwned(item);
            units.OtherBin.mayInventoryAddItemFunc = (item, inv) => units.Sorting && WineTags.IsBerry(item) && WineTags.IsOwned(item);
            units.BottleBin.mayInventoryAddItemFunc = (item, inv) => WineTags.IsEmptyBottle(item) && WineTags.IsOwned(item);
            units.YeastBin.mayInventoryAddItemFunc = (item, inv) => WineTags.IsYeast(item) && WineTags.IsOwned(item);

            // 储水区只允许一个装满水的容器：已有容器时拒绝再放
            units.WaterBin.mayInventoryAddItemFunc = (item, inv) =>
                item != null
                && item.IsTag(WineTags.LiquidContainer)
                && WineTags.IsOwned(item)
                && inv.childItems.Count == 0;

            // 发酵区只收"已经装了料的酒瓶"（发酵中或已成酒）
            units.FermentBin.mayInventoryAddItemFunc = (item, inv) =>
                item != null
                && item.IsTag(WineTags.Container)
                && WineTags.WineState(item) >= WineTags.StateFermenting
                && WineTags.IsOwned(item);

            // ── 控件 ───────────────────────────────────────────────────
            // 按钮文字字号是原版固定值 7px，所以宽度按"字符数×7 + 留白"给就够了：
            // 之前给 50/86/46/40 太大，既占地方又把控制面板撑宽。
            var sortButton = new MachineryButton(Loc.Get("btn_sort", "分拣"), null, 42, 22);
            var brewButton = new MachineryButton(Loc.Get("btn_brew", "开酿"), null, 42, 22);

            // 目标档位：点一下降一档（琼浆玉液 → … → 难以下咽 → 回到琼浆玉液）
            // 六个档位名都是四个汉字（"目标:XXXX" = 7 字符 ≈ 49px），58 够用。
            var targetButton = new MachineryButton("", null, 58, 22);
            // 酵母开关：开 = 必须放酵母；关 = 不放（少 1 分，4 个夜晚出酒）
            var yeastButton = new MachineryButton("", null, 36, 22);
            // 执行类别：强制 / 优先（选中那个加方括号 = 4 字符 ≈ 28px，32 够）
            var forceButton = new MachineryButton("", null, 32, 22);
            var priorityButton = new MachineryButton("", null, 32, 22);

            units.TargetButton = targetButton;
            units.YeastButton = yeastButton;
            units.ForceButton = forceButton;
            units.PriorityButton = priorityButton;

            // 操作反馈用纯文本（没有标签列），才塞得进按钮同一行。
            // 字号必须小 + 文字必须短：这一行是自然宽度，字一多整块控制面板就会变宽，
            // 玩家看到的就是"一点开酿，机器 UI 横向长大一圈"。
            units.HintText = new MachineryText("—", 8, HorizontalAlignmentOptions.Left, false);

            // ── 原版那种「小纸条」说明书 ───────────────────────────────
            // 注意：便签不能在 Create() 里塞进槽位。工厂函数必须产出"干净"的机器，
            // 否则游戏会报 "created with DirectoryMaster contains child items" 并把子物品全部清掉
            // （存档里塞进去的东西也会被牵连）。所以改成开窗/读档后再补放。
            GameSlotInventory noteSlot = new GameSlotInventory();
            MachineHelper.SetupManualSlot(noteSlot);

            // 控制区三行：
            //   1) 分拣 / 开酿 / 便签
            //   2) 目标档位 / 酵母开关
            //   3) 强制 / 优先 / 操作反馈
            var buttonsRow = MachineryStack.Horizontal(new PixelElement[]
            {
                sortButton, brewButton, noteSlot
            });

            var optionRow = MachineryStack.Horizontal(new PixelElement[]
            {
                targetButton, yeastButton
            });

            var modeRow = MachineryStack.Horizontal(new PixelElement[]
            {
                forceButton, priorityButton, units.HintText
            });

            var control = MachineryStack.Vertical(new PixelElement[]
            {
                buttonsRow, optionRow, modeRow
            });

            // 酵母和储水都很小，合成一块面板，面板总数才能压到 8 个 → 正好两排
            MachineryStack supplies = MachineryStack.Horizontal(new PixelElement[]
            {
                units.YeastBin, units.WaterBin
            });

            // ── 窗口布局：8 块面板 / 4 列 = 正好两排 ─────────────────────
            //   第一排：葡萄投放 | 正常果   | 小果      | 其他果
            //   第二排：空酒瓶   | 发酵/成品 | 酵母+储水 | 控制（含说明）
            //
            // 必须用原版 MachineryPanelRow：实测它是唯一能承接
            // 「不开窗直接把物品扔到机器上」的容器（MachineryStack 会让除第一个区域
            // 以外的区域全部收不到投放的物品）。
            MachineryPanel[] panels =
            {
                MachineHelper.Panel(Loc.Get("panel_grape", "葡萄投放"), Plain(units.GrapeInput), "grape", true),
                MachineHelper.Panel(Loc.Get("panel_normal", "正常果"), Plain(units.NormalBin), "normal", true),
                MachineHelper.Panel(Loc.Get("panel_small", "小果"), Plain(units.SmallBin), "small", true),
                MachineHelper.Panel(Loc.Get("panel_other", "其他果"), Plain(units.OtherBin), "other", true),
                MachineHelper.Panel(Loc.Get("panel_bottle", "空酒瓶"), Plain(units.BottleBin), "bottle", true),
                MachineHelper.Panel(Loc.Get("panel_ferment", "发酵 / 成品"), Plain(units.FermentBin), "ferment", true),
                MachineHelper.Panel(Loc.Get("panel_supplies", "酵母 / 储水"), supplies, "supplies", false),
                MachineHelper.Panel(Loc.Get("panel_control", "控制"), control, "control", false)
            };

            // 发酵进度要写进这块面板的标题，先留个引用
            units.FermentPanel = panels[5];

            PixelWindow window;
            try
            {
                // 自由行布局：两排各自决定高度，第二排就真的能比第一排矮。
                // 代价是不能用 MachineryPanelRow（它会把所有面板 ResizePixels 成同一尺寸），
                // 而换了根节点后原版「不开窗直接扔到机器上」的分发会失效，
                // 所以下面用 canItemTargetThisFunc / onItemTargetThisFunc 自己接管投放。
                MachineryStack topRow = MachineryStack.Horizontal(new PixelElement[]
                {
                    panels[0], panels[1], panels[2], panels[3]
                });

                MachineryStack bottomRow = MachineryStack.Horizontal(new PixelElement[]
                {
                    panels[4], panels[5], panels[6], panels[7]
                });

                MachineryStack root = MachineryStack.Vertical(new PixelElement[] { topRow, bottomRow });

                window = new PixelWindow(true, Loc.Get("machine_name", "酿酒便利机"), WindowStyle.Machinery);
                window.Attach(root);
            }
            catch (Exception e)
            {
                ModEntry.Warn("自由行布局构建失败，退回等宽表格: " + e.Message);
                window = MachineHelper.CreateMachineryWindow(true, panels);
                try
                {
                    var fallbackRow = window.childElement as MachineryPanelRow;
                    if (fallbackRow != null) fallbackRow.SetColumns(4);
                }
                catch (Exception) { }
            }

            // ── 机器本体 ───────────────────────────────────────────────
            GameItem machine = ItemDirectory.CreateEmptyItem(ItemId);
            machine.SetContentWindow(window);
            ApplySprite(machine);
            ApplyLocalizedNames(machine);

            machine.unitValue = 888L;
            machine.SetGameItemType("MACHINE");
            machine.EnableTag("CONTAINER_TAG");
            machine.EnableTag("IMPORTANT_TAG");

            // ── 交互 ───────────────────────────────────────────────────
            units.Machine = machine;
            sortButton.SetCallback(() => units.Sort());
            brewButton.SetCallback(() => units.Brew());
            targetButton.SetCallback(() => units.CycleTier());
            yeastButton.SetCallback(() => units.ToggleYeast());
            forceButton.SetCallback(() => units.SetForce(true));
            priorityButton.SetCallback(() => units.SetForce(false));

            // 「不开窗直接把物品扔到机器上」——自己按类型分发到对应区域。
            // 原版是靠窗口的关联存储自动找位置，换成自由行布局后那条链断了，
            // 所以改用物品对物品的投放回调（原版倒水、工具箱也是用这一对）。
            machine.canItemTargetThisFunc = (item, self) => units.CanTake(item);
            machine.onItemTargetThisFunc = (item, self) => units.Take(item);

            units.RefreshButtons();

            window.openCallback = () =>
            {
                EnsureManual(noteSlot);
                units.Refresh(true);
            };
            machine.onLoaded = m => EnsureManual(noteSlot);

            // 文本只在"真的可能变了"的时候刷：开窗、区域物品增删、每日结算。
            // 绝对不能挂到 onOneSecondItemFunc 上 —— 状态文字变长变短会让整行面板重新量宽并重排，
            // 每秒一次会把主线程压住（实测触发过 60 秒的冻结）。
            Action<GameItem, GameInventory, SlotMarker> onSlotChanged = (i, inv, slot) => units.Refresh();

            GameInventory[] bins =
            {
                units.GrapeInput, units.NormalBin, units.SmallBin, units.OtherBin,
                units.BottleBin, units.YeastBin, units.WaterBin, units.FermentBin
            };
            foreach (GameInventory bin in bins)
            {
                bin.onSlotAddItemEndFunc = onSlotChanged;
                bin.onSlotRemoveItemEndFunc = onSlotChanged;
            }

            // 投放区：物品一落地就分流到对应区域。
            // 必须放在上面的循环之后 —— 否则会被统一刷新回调覆盖掉。
            units.GrapeInput.onSlotAddItemEndFunc = (i, inv, slot) =>
            {
                units.RouteFromIntake();
                units.Refresh();
            };

            machine.onCycleEndSlotItemFunc = (m, inv, slot) => units.Refresh();

            return machine;
        }

        /// <summary>说明书正文（写死在代码里，不走语言表：CSV 存不了多行文本）。</summary>
        private const string ManualBody =
            "<size=60%>【三个按钮】\n" +
            "「目标」：点一下降一档，琼浆玉液 → 臻品窖藏 → 量产酒液 → 手工自酿 → 粗桶劣酿 → 难以下咽，到底后回到最高档。\n" +
            "「酵母 开/关」：关掉后少 1 分，出酒时间从 2 个夜晚变成 4 个夜晚。\n" +
            "「强制 / 优先」：强制 = 最终分值必须落在目标档，差一分都不酿；优先 = 先按目标档酿，凑不出就自动往下降一档，最低只降到量产酒液。\n\n" +
            "【配比】\n" +
            "两颗葡萄分值之和 + 1（≥2 颗）+ 1（有酵母）+ 水质分。\n" +
            "优质水 +1，基础水 0，幽灵水及以下为负。\n" +
            "三个果区都会取用：正常果 / 小果酿高档；其他果（未熟·干枯·发霉）分值低，只会在低档时被选中。\n\n" +
            "【用法】\n" +
            "葡萄丢进投放区 → 分拣 → 放空瓶、酵母、一个装水的容器 → 开酿。发酵区最多同时 8 瓶。\n\n" +
            "注意：卖掉或丢掉这台机器，里面的东西会一起消失。</size>";

        /// <summary>
        /// 保证便签槽里有一张说明书。只在开窗 / 读档后调用，
        /// 绝不能放进工厂（那会让机器"出生即带子物品"，游戏会把子物品清空）。
        /// </summary>
        private static void EnsureManual(GameSlotInventory slot)
        {
            if (slot == null) return;

            try
            {
                if (slot.childItems.Count > 0) return;

                GameItem manual = DirectoryMaster.Item("postit", true);
                manual.name = Loc.Get("doc_title", "使用说明：酿酒便利机");
                manual.shortDescription = Loc.Get("doc_short", "酿酒便利机的使用说明（点开查看）");
                manual.flavorText = ManualBody;
                manual.SetGameItemType("DOCUMENT");
                manual.EnableTag("IMPORTANT_TAG");
                manual.EnableTag("MACHINE_MANUAL_TAG");
                manual.unitValue = 0L;

                slot.UncheckedAccept(manual);
            }
            catch (Exception e)
            {
                ModEntry.Warn("放置说明书便签失败: " + e.Message);
            }
        }

        /// <summary>
        /// 把网格再套一层空 stack 后交给面板。
        /// 直接把 GameInventory 传给 MachineHelper.Panel 会被 WrapInIntake 包一层带
        /// 「通风格栅」的外壳，面板一旦被行高拉伸，多出来的空间就会露出上下两条格栅条纹。
        /// 套一层 stack 可以绕开自动包装，面板背景就干净了。
        /// </summary>
        private static PixelElement Plain(GameGridInventory grid)
        {
            return MachineryStack.Horizontal(new PixelElement[] { (PixelElement)grid });
        }

        /// <summary>说明面板里的一行小字。</summary>
        private static MachineryText HelpLine(string key, string fallback)
        {
            return new MachineryText(Loc.Get(key, fallback), 10, HorizontalAlignmentOptions.Left, false);
        }

        /// <summary>贴图 + 占格。贴图缺失时退回原版 3x3 的葡萄酒架贴图，保证一定能摆出来。</summary>
        private static void ApplySprite(GameItem machine)
        {
            bool ok = false;
            try
            {
                machine.SetSpriteAndShapeFromMod(ModEntry.ModId, SpriteKey);
                ok = machine.shape != null && machine.shape.width > 0;
            }
            catch (Exception)
            {
                ok = false;
            }

            if (!ok)
            {
                try { machine.SetSpriteAndShape("Items/items_winemaking", "wine_rack"); }
                catch (Exception) { }
            }

            // 无论如何都锁死 3x3 占地，和原版酒架一致
            try { machine.SetShape(new GridShapeBuilder(3, 3).SetDataFill(1).Build()); }
            catch (Exception) { }
        }

        public static void ApplyLocalizedNames(GameItem machine)
        {
            if (machine == null) return;
            try
            {
                machine.name = Loc.Get("machine_name", "酿酒便利机");
                machine.shortDescription = Loc.Get("machine_desc",
                    "把葡萄丢进投放区、点一下分拣，再点一下开酿：自动配比、自动灌水、自动发酵。");
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 读档后重挂一次外观与名称。工厂可能在模组贴图/语言表就绪之前跑过
        /// （那时会退到原版贴图和兜底文案），这里补一次是最省事的修正点。
        /// </summary>
        public static void RefreshAppearance(GameItem machine)
        {
            if (machine == null) return;

            try
            {
                machine.SetSpriteAndShapeFromMod(ModEntry.ModId, SpriteKey);
                machine.SetShape(new GridShapeBuilder(3, 3).SetDataFill(1).Build());
            }
            catch (Exception)
            {
                // 贴图没加载成功就保持原样，不影响功能
            }

            ApplyLocalizedNames(machine);
        }
    }
}
