using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 将用户自定义值应用到设备展示快照。
/// 2026/10/18 12:19:45 20211
/// </summary>
public static class DevicePresentation
{
    public static DeviceStatus Apply(DeviceStatus source, AppSettings settings)
        => DeviceCustomizationResolver.Apply(source, settings.DeviceCustomizations);

    public static bool HasIconOverride(string id, AppSettings settings) =>
        settings.DeviceCustomizations?.TryGetValue(id, out var custom) == true && custom.IconKind.HasValue;

    public static string DisplayName(string id, string fallback, AppSettings settings) =>
        DeviceCustomizationResolver.DisplayName(id, fallback, settings.DeviceCustomizations);
}
