namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 更新方式的三档取值与文案：Auto（有更新就下载安装并重启）/ Notify（仅通知，等用户确认）/ Disabled（启动不检查）。
/// 2026/10/18 20:10:00 xliu
/// 手动「检查更新」按钮不受 Disabled 限制——那是用户主动发起的动作。
/// </summary>
public static class UpdateMode
{
    public const string Auto = "Auto";
    public const string Notify = "Notify";
    public const string Disabled = "Disabled";

    /// <summary>下拉框档位（顺序即界面展示顺序）。</summary>
    public static IReadOnlyList<(string Value, string Label)> Choices { get; } =
    [
        (Auto, "有更新时自动更新"),
        (Notify, "有更新时仅通知"),
        (Disabled, "禁用更新"),
    ];

    /// <summary>把任意输入（含 null / 手改坏了的配置）收敛成三个合法取值之一。</summary>
    public static string Normalize(string? value) => value switch
    {
        Auto => Auto,
        Disabled => Disabled,
        _ => Notify,
    };

    /// <summary>设置项副标题用的一句话说明。</summary>
    public static string Describe(string? value) => Normalize(value) switch
    {
        Auto => "启动时检查，发现新版本直接下载安装并自动重启",
        Disabled => "启动时不检查更新（仍可手动点「检查更新」）",
        _ => "启动时检查，发现新版本先提醒，由你决定是否安装",
    };
}
