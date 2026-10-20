using VigilWatt.App.ViewModels;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using VigilWatt.Core.Settings;
using Microsoft.UI.Xaml.Navigation;
using VigilWatt.App.Services;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace VigilWatt.App.Views;
/// <summary>
/// 功 能： 设备电量列表页。
/// 2026/10/06 15:53:36 xliu
/// </summary>

public sealed partial class DevicesPage : Page
{
    /// <summary>手动刷新进行中：挡住重复点击，也让"刷新中"状态不被后台事件提前清掉。</summary>
    private bool _refreshing;

    /// <summary>事件是否已订阅：Loaded 可能重复触发，不挡住就会叠加回调。</summary>
    private bool _hooked;

    /// <summary>同名合并提示：已经提示过的设备组合（避免每次轮询都弹）。</summary>
    private readonly HashSet<string> _mergePrompted = new(StringComparer.Ordinal);

    /// <summary>同屏只允许一个合并提示弹窗。</summary>
    private bool _mergePromptOpen;

    public DevicesPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    // 页面被缓存复用、构造函数只跑一次：订阅与刷新必须跟着 Loaded/Unloaded 走，
    // 否则第一次切走就解绑，切回来不再走构造函数，页面从此收不到设备事件也不再刷新
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked)
        {
            _hooked = true;
            App.Services.Manager.DevicesChanged += OnDevicesChanged;
            App.Services.Manager.DeviceUpdated += OnDeviceUpdated;
        }
        Refresh();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked) return;
        _hooked = false;
        App.Services.Manager.DevicesChanged -= OnDevicesChanged;
        App.Services.Manager.DeviceUpdated -= OnDeviceUpdated;
    }

    private void OnDeviceUpdated(DeviceStatus _) => OnDevicesChanged();

    private void OnDevicesChanged() => RequestRefresh();

    private int _refreshQueued;

    /// <summary>
    /// 合并刷新请求（CR-29）：一轮轮询会对 N 台设备各发一次 DeviceUpdated、外加一次 DevicesChanged，
    /// 逐个排队就是 N+1 次全量重建 ItemsSource。用 Interlocked 只保留一次待执行的刷新。
    /// </summary>
    private void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);   // 先复位再刷新：刷新期间来的请求不会被吞掉
            Refresh();
        });
    }

    /// <summary>上一次渲染的指纹：设备集合/电量/颜色/顺序都没变时不重建 ItemsSource——
    /// 重建会作废全部 item 容器（每行 10+ 个 XAML 元素），合成器内存只涨不还。</summary>
    private string? _rowsKey;

    private void Refresh()
    {
        var devices = App.Services.Manager.Devices;
        var settings = App.Services.Settings;
        var groups = App.Services.Manager.MergeGroups;
        // 排序表只该存原始设备 ID；旧版本曾把组合 ID（a+b）写进去，留着会让多选/排序判定混乱，这里顺手清掉
        if (settings.DeviceOrder.RemoveAll(id => id.Contains('+')) > 0) settings.Save();
        var merged = DeviceMergeResolver.Build(devices, groups, settings.DeviceOrder,
                                               settings.DeviceCustomizations);

        string key = BuildRowsKey(merged, settings);
        if (key != _rowsKey || DeviceList.ItemsSource == null)
        {
            _rowsKey = key;
            DeviceList.ItemsSource = merged.Select(d => DeviceRowModel.From(d, settings)).ToList();
        }

        var online = merged.Count(d => d.IsOnline);
        // 带上时间戳：即使设备数据没变，用户也能一眼看出这次刷新确实跑过了
        SummaryText.Text = $"{online} 台在线 / 共 {merged.Count} 台 · {DateTime.Now:HH:mm:ss} 更新";

        bool empty = merged.Count == 0;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        SuggestSameNameMerge(merged, settings);
    }

    /// <summary>
    /// 名字相同但还没合并的设备，提示一次是否合并（用户明确要求：名字一样就直接合并）。
    /// 每对只提示一次（进程内记忆），拒绝后不再打扰；用户改完名再刷新会重新触发。
    /// </summary>
    private async void SuggestSameNameMerge(IReadOnlyCollection<MergedDevice> rows, AppSettings settings)
    {
        if (_mergePromptOpen) return;

        var candidate = rows
            .Where(r => !r.IsMerged && r.Primary.Source != DeviceSource.System)
            .GroupBy(r => DevicePresentation.Apply(r.Primary, settings).DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0 && g.Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate == null) return;

        var ids = candidate.Select(r => r.Primary.Id).ToArray();
        string promptKey = DeviceMergeResolver.GroupId(ids);
        if (!_mergePrompted.Add(promptKey)) return;

        // 让当前这一帧先画出来，再弹窗（Refresh 常在事件回调里同步跑）
        await Task.Yield();
        if (_mergePromptOpen) return;
        _mergePromptOpen = true;
        try
        {
            string detail = string.Join("\n", candidate.Select(r =>
                $"· {DeviceSourceInfo.ShortLabel(r.Primary.Source)}（{(r.IsOnline ? "在线" : "离线")}）"));
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "合并为同一台设备？",
                Content = $"检测到 {candidate.Count()} 条记录名称相同：\n\n{detail}\n\n" +
                          "合并后只显示一行，徽标逐个标注各通道是否在线；历史曲线也会合并显示。随时可以右键拆分。",
                PrimaryButtonText = "合并",
                CloseButtonText = "暂不合并",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
                App.Services.Manager.MergeDevices(ids))
            {
                // 同名提示由"被合并"的一方接住主动方的外观：ids 按行序，先出现的那条是主动方
                var target = ids[ids.Length - 1];
                AdoptCustomization(App.Services.Manager.GroupOf(target) ?? ids, target);
                App.Services.Manager.RefreshPresentation();
                Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Error("同名设备合并提示失败", ex);
        }
        finally
        {
            _mergePromptOpen = false;
        }
    }

    /// <summary>行内容指纹：参与显示的字段全在里面（不含"更新于"时间，否则每轮都不同）。</summary>
    private static string BuildRowsKey(IReadOnlyCollection<MergedDevice> devices, AppSettings settings)
    {
        var sb = new System.Text.StringBuilder(128);
        foreach (var source in devices)
        {
            var d = DevicePresentation.Apply(source, settings);
            sb.Append(source.Id).Append('|')
              .Append(d.DisplayName).Append('|')
              .Append((int)DevicePresentation.KindOf(source, settings)).Append('|')
              .Append(d.Protocol).Append('|')
              .Append(d.BatteryPercent?.ToString() ?? "-").Append('|')
              .Append((int)d.Charging).Append('|')
              .Append(source.IsOnline ? '1' : '0').Append('|')
              // 每个通道的在线状态也算进指纹：合并组的徽标颜色/灰显会随它变
              .Append(string.Join(",", source.Channels.Select(c => c.IsOnline ? '1' : '0'))).Append('|')
              // 压制通道（蓝牙在线时的 2.4G）同样影响徽标与副标题
              .Append(string.Join(",", source.Suppressed.Select(c => c.Id)))
              .Append('|')
              .Append(DevicePresentation.AccentColor(source) is { } c ? UiPalette.ToHex(c) : "-")
              .Append(';');
        }
        return sb.ToString();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveDevice(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveDevice(sender, +1);

    /// <summary>删除离线设备：列表条目、自定义信息、排序记录一并清掉；在线设备按钮不显示，这里再兜一次底。
    /// 合并组会删掉组内**所有**离线通道（组里还有在线成员时整行仍保留）。</summary>
    private void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id) return;
        var members = ResolveMemberIds(id);
        var ids = members.Where(x => App.Services.Manager.Find(x) is { IsOnline: false }).ToList();
        if (ids.Count == 0) return;
        foreach (var member in ids) App.Services.Manager.RemoveOfflineDevice(member);
        Refresh();
    }

    /// <summary>
    /// 行 ID 可能是合并组的组合 ID，拆回真实设备 ID。
    /// 先按组合 ID 精确匹配；匹配不上再按**成员交集**回退——组里可能残留已不在列表的 ID，
    /// 那时行的组合 ID 只由现存成员拼成，与组完整拼出来的字符串不相等（CR-71 真机复现：
    /// 成员为空，删除/改名一律静默失效）。
    /// </summary>
    private static IReadOnlyList<string> ResolveMemberIds(string rowId)
    {
        var groups = App.Services.Manager.MergeGroups;
        var exact = groups.FirstOrDefault(g => DeviceMergeResolver.GroupId(g) == rowId);
        if (exact is not null) return exact.Where(x => App.Services.Manager.Find(x) != null).ToList();

        // 回退：把行 ID 拆开，取与它相交的那一组（组内幽灵成员不参与拼接，因此对不上）
        var parts = rowId.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var hit = groups.FirstOrDefault(g => g.Intersect(parts, StringComparer.Ordinal).Any());
        var members = (hit ?? (IReadOnlyList<string>)parts)
                      .Where(x => App.Services.Manager.Find(x) != null)
                      .ToList();
        return members;
    }

    /// <summary>手动排序：在 DeviceOrder 里移一位并落盘，然后让 Manager 广播一次，把新顺序同步到贴片/小组件/托盘。</summary>
    private void MoveDevice(object sender, int delta)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id) return;
        App.Services.Manager.MoveInOrder(id, delta);
        App.Services.Manager.RefreshPresentation();
        Refresh();
    }

    private async void CustomizeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string rowId) return;
        // 行 ID 可能是合并组的组合 ID：外观是**整组共用**的，写回时摊给每个成员，
        // 否则换通道（2.4G ↔ 蓝牙）后名称/图标就会跳回原值（见 MergedCustomization）
        var ids = ResolveMemberIds(rowId).Where(x => App.Services.Manager.Find(x) != null).ToList();
        if (ids.Count == 0) return;
        var device = App.Services.Manager.Find(ids[0])!;

        var settings = App.Services.Settings;
        // 预填组内共用外观；整组都没定制过时用行上看到的显示名（不是某个成员的原始名）
        var current = MergedCustomization.Resolve(ids, settings.DeviceCustomizations);
        var nameBox = new TextBox
        {
            Text = current?.Name ?? DevicePresentation.DisplayName(device.Id, device.DisplayName, settings),
            MaxLength = 80,
            PlaceholderText = "输入设备名称",
        };
        var nameError = new TextBlock
        {
            Text = "名称不能为空。",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
            Visibility = Visibility.Collapsed,
        };
        nameBox.TextChanged += (_, _) => nameError.Visibility = Visibility.Collapsed;

        // "自动"预览用组内共用的类型（合并组可能与首个成员的声明类型不同）
        var automaticKind = device.Kind == DeviceKind.Unknown
            ? DeviceKindGuesser.Guess(device.DisplayName)
            : device.Kind;
        DeviceKind? selectedKind = current?.IconKind;
        var iconChoices = new List<(RadioButton Button, Border Card)>();
        var iconGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int i = 0; i < 4; i++)
            iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 2; i++)
            iconGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var options = new (DeviceKind? Value, DeviceKind Preview, string Label)[]
        {
            (null, automaticKind, "自动"), (DeviceKind.Unknown, DeviceKind.Unknown, "通用"),
            (DeviceKind.Mouse, DeviceKind.Mouse, "鼠标"), (DeviceKind.Keyboard, DeviceKind.Keyboard, "键盘"),
            (DeviceKind.Headset, DeviceKind.Headset, "耳机"), (DeviceKind.Speaker, DeviceKind.Speaker, "音箱"),
            (DeviceKind.Gamepad, DeviceKind.Gamepad, "手柄"), (DeviceKind.Computer, DeviceKind.Computer, "电脑"),
        };
        var primaryBrush = Application.Current.Resources["TextFillColorPrimaryBrush"] as SolidColorBrush;
        var iconColor = primaryBrush?.Color ?? Color.FromArgb(255, 80, 80, 80);
        Brush GetBrush(string key, Brush fallback) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush ? brush : fallback;
        var cardBrush = GetBrush("CardBackgroundFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(255, 248, 248, 248)));
        var borderBrush = GetBrush("CardStrokeColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(255, 220, 220, 220)));
        var accentBrush = GetBrush("AccentFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)));
        var accentColor = (accentBrush as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0, 120, 212);
        var accentTint = new SolidColorBrush(Color.FromArgb(28, accentColor.R, accentColor.G, accentColor.B));

        for (int i = 0; i < options.Length; i++)
        {
            var option = options[i];
            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 9, 4, 8),
                Background = cardBrush,
                BorderBrush = borderBrush,
                Child = new StackPanel
                {
                    Spacing = 6,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new Grid
                        {
                            Width = 28,
                            Height = 28,
                            Children = { DeviceIconFactory.Glyph(option.Preview, iconColor) },
                        },
                        new TextBlock
                        {
                            Text = option.Label,
                            FontSize = 12,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };
            var radio = new RadioButton
            {
                GroupName = "DeviceIconChoice",
                Tag = option.Value,
                IsChecked = selectedKind == option.Value,
                Content = card,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                MinWidth = 0,
            };
            radio.Checked += (_, _) =>
            {
                selectedKind = option.Value;
                UpdateIconChoiceCards();
            };
            iconChoices.Add((radio, card));
            Grid.SetColumn(radio, i % 4);
            Grid.SetRow(radio, i / 4);
            iconGrid.Children.Add(radio);
        }

        void UpdateIconChoiceCards()
        {
            foreach (var (radio, card) in iconChoices)
            {
                bool selected = radio.IsChecked == true;
                card.BorderBrush = selected ? accentBrush : borderBrush;
                card.Background = selected ? accentTint : cardBrush;
            }
        }
        UpdateIconChoiceCards();

        // ---- 颜色（16 色预设）----
        // 新设备统一用默认色（黑 = 不自定义颜色）；已有自定义色的设备预选回来。
        string? selectedColorHex = UiPalette.TryParseHex(current?.ColorHex) is { } preset ? UiPalette.ToHex(preset) : null;

        var colorChoices = new List<(RadioButton Button, Border Ring, FontIcon Check)>();
        var colorGrid = new Grid { ColumnSpacing = 6, RowSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 4; i++)
        {
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        var clearBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        var checkBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        for (int i = 0; i < UiPalette.CustomColorChoices.Length; i++)
        {
            var swatchColor = UiPalette.CustomColorChoices[i];
            var hex = UiPalette.ToHex(swatchColor);
            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 13,
                Foreground = checkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var dot = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(swatchColor),
                Child = check,
            };
            var ring = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderThickness = new Thickness(2),
                BorderBrush = clearBrush,
                Child = dot,
            };
            var radio = new RadioButton
            {
                GroupName = "DeviceColorChoice",
                Tag = hex,
                IsChecked = string.Equals(hex, selectedColorHex, StringComparison.OrdinalIgnoreCase),
                Content = ring,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
            };
            radio.Checked += (_, _) =>
            {
                selectedColorHex = hex;
                UpdateColorChoiceCards();
            };
            colorChoices.Add((radio, ring, check));
            Grid.SetColumn(radio, i % 4);
            Grid.SetRow(radio, i / 4);
            colorGrid.Children.Add(radio);
        }

        void UpdateColorChoiceCards()
        {
            foreach (var (radio, ring, check) in colorChoices)
            {
                bool selected = radio.IsChecked == true;
                ring.BorderBrush = selected ? accentBrush : clearBrush;
                check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        UpdateColorChoiceCards();

        var body = new StackPanel { Spacing = 10, MinWidth = 400 };
        body.Children.Add(new TextBlock { Text = "设备名称", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(nameBox);
        body.Children.Add(nameError);
        body.Children.Add(new TextBlock
        {
            Text = "图标",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        body.Children.Add(iconGrid);
        body.Children.Add(new TextBlock
        {
            Text = "颜色（数字与电量条）",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 0),
        });
        body.Children.Add(colorGrid);
        var resetColorButton = new HyperlinkButton
        {
            Content = "使用默认色（黑）",
            FontSize = 12,
            Padding = new Thickness(0, 2, 0, 0),
        };
        resetColorButton.Click += (_, _) =>
        {
            selectedColorHex = null;
            foreach (var (radio, _, _) in colorChoices) radio.IsChecked = false;
            UpdateColorChoiceCards();
        };
        body.Children.Add(resetColorButton);
        body.Children.Add(new TextBlock
        {
            Text = "点「恢复默认」可取消自定义名称、图标和颜色。",
            FontSize = 12,
            Foreground = Application.Current.Resources["TextFillColorTertiaryBrush"] as Brush,
        });
        // 内容较高（名称 + 8 图标 + 16 色）：套 ScrollViewer，否则超出对话框可视区后下面的颜色点不到
        var scroller = new ScrollViewer
        {
            Content = body,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设备外观",
            Content = scroller,
            PrimaryButtonText = "保存",
            SecondaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                nameError.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            MergedCustomization.ApplyToGroup(ids, settings.DeviceCustomizations, null);
        }
        else
        {
            var name = nameBox.Text.Trim();
            var custom = new DeviceCustomization();
            custom.Name = name;
            custom.IconKind = selectedKind;
            custom.ColorHex = selectedColorHex;
            // 合并组内所有设备跟随同一份外观；拆分后各通道也各自保留这套值
            MergedCustomization.ApplyToGroup(ids, settings.DeviceCustomizations, custom);
        }

        settings.Save();
        App.Services.Manager.RefreshPresentation();
        Refresh();
    }

    /// <summary>
    /// 合并后让外观"跟随被合并的设备"：主动方整组的外观全部让位给目标设备的那一份。
    /// 顺序按用户语义——目标设备（"合并到"的那台）赢，主动方原有的名称/图标/颜色一律覆盖。
    /// </summary>
    private static void AdoptCustomization(IReadOnlyList<string> mergedGroup, string targetId)
    {
        var settings = App.Services.Settings;
        var map = settings.DeviceCustomizations;

        // 目标设备所在组（若它自己也已是一组）里共用的那份，才是"被合并设备"的外观
        var targetMembers = App.Services.Manager.GroupOf(targetId) ?? new[] { targetId };
        var source = MergedCustomization.Resolve(targetMembers, map);

        // 目标自己都没定制过 ⇒ 整组统一恢复默认，不能让主动方那套残留下来
        MergedCustomization.ApplyToGroup(mergedGroup, map,
            MergedCustomization.HasAnyValue(source) ? source : null);
        settings.Save();
    }

    private void DeviceRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 别用 row.DataContext 强转：命中的可能是模板里的子元素，指针落在 Border 留白或
        // Grid 空白区时拿不到行模型，右键就"没反应"。以元素自身（含祖先）为准。
        if (sender is not FrameworkElement row || RowModelOf(row) is not { } model) return;
        var members = ResolveMemberIds(model.Id);
        if (App.Services.Manager.Find(members[0]) == null) return;

        var menu = new MenuFlyout();

        // 合并 / 拆分：同一台物理设备的 2.4G 与蓝牙通道合到一行
        if (model.IsMerged)
        {
            var split = new MenuFlyoutItem { Text = "拆分此设备（恢复成多行）", Tag = model.Id };
            split.Click += SplitDevice_Click;
            menu.Items.Add(split);
        }
        else
        {
            var settings = App.Services.Settings;
            // 已在别的合并组里的设备不再作为合并候选：它们已经在列表里以一行显示，
            // 再列出来会让人以为还没合并（也与"合并过就只显示一个"的预期冲突）
            var groups = App.Services.Manager.MergeGroups;
            var others = App.Services.Manager.Devices
                .Where(d => !members.Contains(d.Id) && !groups.Any(g => g.Contains(d.Id, StringComparer.Ordinal)))
                .ToList();
            if (others.Count > 0)
            {
                var merge = new MenuFlyoutSubItem { Text = "合并到其他设备…" };
                foreach (var other in others)
                {
                    // 必须用**用户改过的名字**：设备原始名可能是同一串型号（两只 MCHOSE G3 A、两串 MAC），
                    // 只显示原名分不清谁是谁
                    var item = new MenuFlyoutItem
                    {
                        Text = $"{DevicePresentation.DisplayName(other.Id, other.DisplayName, settings)}" +
                               $"（{DeviceSourceInfo.ShortLabel(other.Source)}）{(other.IsOnline ? "" : " · 离线")}",
                        Tag = new[] { members[0], other.Id },
                    };
                    item.Click += MergeDevice_Click;
                    merge.Items.Add(item);
                }
                menu.Items.Add(merge);
            }
        }

        var customize = new MenuFlyoutItem
        {
            Text = "自定义图标、名称和颜色",
            Tag = model.Id,   // 行 ID：合并组要写进组内实际成员，见 CustomizeDevice_Click
        };
        customize.Click += CustomizeDevice_Click;
        menu.Items.Add(customize);

        menu.ShowAt(row, new FlyoutShowOptions { Position = e.GetPosition(row) });
        e.Handled = true;
    }

    /// <summary>沿可视树向上找一个带 <see cref="DeviceRowModel"/> 的元素（右键命中的往往是模板子元素）。</summary>
    private static DeviceRowModel? RowModelOf(DependencyObject start)
    {
        for (var node = start; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: DeviceRowModel model }) return model;
        return null;
    }

    /// <summary>把本行合并到用户选中的另一台设备。</summary>
    private async void MergeDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string[] ids || ids.Length < 2) return;

        var a = App.Services.Manager.Find(ids[0]);
        var b = App.Services.Manager.Find(ids[1]);
        if (a == null || b == null) return;

        var settings = App.Services.Settings;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "合并设备",
            Content = $"将以下两条记录合并为一台设备？\n\n" +
                      $"· {DevicePresentation.DisplayName(a.Id, a.DisplayName, settings)}（{DeviceSourceInfo.ShortLabel(a.Source)}）\n" +
                      $"· {DevicePresentation.DisplayName(b.Id, b.DisplayName, settings)}（{DeviceSourceInfo.ShortLabel(b.Source)}）\n\n" +
                      "合并后只显示一行，徽标逐个标注各通道是否在线；历史曲线也会合并显示。随时可以拆分。",
            PrimaryButtonText = "合并",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        // ids[1] 是用户选中的"合并到"目标设备：合并后整组外观跟随它
        var target = ids[ids.Length - 1];
        if (App.Services.Manager.MergeDevices(ids))
        {
            var group = App.Services.Manager.GroupOf(target) ?? ids;
            AdoptCustomization(group, target);
            App.Services.Manager.RefreshPresentation();
            Refresh();
        }
    }

    private void SplitDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string rowId) return;
        if (App.Services.Manager.SplitDevice(ResolveMemberIds(rowId))) Refresh();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;

        _refreshing = true;
        SetRefreshing(true);
        var startedAt = DateTimeOffset.Now;
        try
        {
            // 等整轮跑完（含 BLE 异步补读）。若自动轮询正在进行，这里会复用那一轮，不会白跑也不会被丢弃。
            await App.Services.Manager.RequestPollAsync();
        }
        catch (Exception ex)
        {
            Log.Error("手动刷新失败", ex);
        }
        finally
        {
            // 保证"刷新中"至少可见一小会儿：否则本地扫描几十毫秒就结束，按钮闪一下反而像没反应
            var elapsed = DateTimeOffset.Now - startedAt;
            var minVisible = TimeSpan.FromMilliseconds(450);
            if (elapsed < minVisible)
                await Task.Delay(minVisible - elapsed);

            Refresh();
            SetRefreshing(false);
            _refreshing = false;
        }
    }

    private void SetRefreshing(bool on)
    {
        RefreshButton.IsEnabled = !on;
        RefreshRing.IsActive = on;
        RefreshRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshIcon.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RefreshText.Text = on ? "刷新中…" : "刷新";
    }
}
