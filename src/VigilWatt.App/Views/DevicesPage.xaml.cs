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

        string key = BuildRowsKey(devices, settings);
        if (key != _rowsKey || DeviceList.ItemsSource == null)
        {
            _rowsKey = key;
            DeviceList.ItemsSource = devices.Select(d => DeviceRowModel.From(d, settings)).ToList();
        }

        var online = devices.Count(d => d.IsOnline);
        // 带上时间戳：即使设备数据没变，用户也能一眼看出这次刷新确实跑过了
        SummaryText.Text = $"{online} 台在线 / 共 {devices.Count} 台 · {DateTime.Now:HH:mm:ss} 更新";

        bool empty = devices.Count == 0;
        DeviceList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>行内容指纹：参与显示的字段全在里面（不含"更新于"时间，否则每轮都不同）。</summary>
    private static string BuildRowsKey(IReadOnlyCollection<DeviceStatus> devices, AppSettings settings)
    {
        var sb = new System.Text.StringBuilder(128);
        foreach (var source in devices)
        {
            var d = DevicePresentation.Apply(source, settings);
            sb.Append(d.Id).Append('|')
              .Append(d.DisplayName).Append('|')
              .Append((int)d.Kind).Append('|')
              .Append(d.Protocol).Append('|')
              .Append(d.BatteryPercent?.ToString() ?? "-").Append('|')
              .Append((int)d.Charging).Append('|')
              .Append(d.IsOnline ? '1' : '0').Append('|')
              .Append(DevicePresentation.AccentColor(source.Id, settings) is { } c ? UiPalette.ToHex(c) : "-")
              .Append(';');
        }
        return sb.ToString();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveDevice(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveDevice(sender, +1);

    /// <summary>删除离线设备：列表条目、自定义信息、排序记录一并清掉；在线设备按钮不显示，这里再兜一次底。</summary>
    private void DeleteDevice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string id) return;
        if (!App.Services.Manager.RemoveOfflineDevice(id)) return;
        Refresh();
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
        if (sender is not MenuFlyoutItem item || item.Tag is not string id) return;
        var device = App.Services.Manager.Find(id);
        if (device == null) return;

        var settings = App.Services.Settings;
        settings.DeviceCustomizations.TryGetValue(id, out var current);
        var nameBox = new TextBox
        {
            Text = current?.Name ?? device.DisplayName,
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
            settings.DeviceCustomizations.Remove(id);
        }
        else
        {
            var name = nameBox.Text.Trim();
            var custom = new DeviceCustomization();
            custom.Name = name;
            custom.IconKind = selectedKind;
            custom.ColorHex = selectedColorHex;
            settings.DeviceCustomizations[id] = custom;
        }

        settings.Save();
        App.Services.Manager.RefreshPresentation();
        Refresh();
    }

    private void DeviceRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row || row.DataContext is not DeviceRowModel model) return;
        var device = App.Services.Manager.Find(model.Id);
        if (device == null) return;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "自定义图标、名称和颜色",
            Tag = model.Id,
        });
        var item = (MenuFlyoutItem)menu.Items[0];
        item.Click += CustomizeDevice_Click;
        menu.ShowAt(row, new FlyoutShowOptions { Position = e.GetPosition(row) });
        e.Handled = true;
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
