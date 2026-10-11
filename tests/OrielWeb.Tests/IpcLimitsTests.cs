using OrielWeb.Ipc;
using Xunit;

namespace OrielWeb.Tests;

/// <summary>
/// 入站 IPC 的规模闸。
/// </summary>
/// <remarks>
/// 回归防护：评审 P3 —— 三平台入站都是「整串读进托管内存 → <c>JsonDocument.Parse</c>」，
/// 此前没有任何上限（可信页面 DoS 刻意不在防线内，但"一条大消息拖住主线程"不该连闸都没有）。
/// 闸本身是纯函数，边界值在这里钉住：恰好等于上限放行、多一个字符即拒。
/// </remarks>
public sealed class IpcLimitsTests
{
    [Fact]
    public void NullOrEmpty_IsWithinLimit()
    {
        Assert.False(OrielIpcLimits.ExceedsLimit(null));
        Assert.False(OrielIpcLimits.ExceedsLimit(string.Empty));
    }

    [Fact]
    public void ExactlyAtLimit_IsWithinLimit()
        => Assert.False(OrielIpcLimits.ExceedsLimit(new string('x', OrielIpcLimits.MaxInboundMessageChars)));

    [Fact]
    public void OneCharOverLimit_IsRejected()
        => Assert.True(OrielIpcLimits.ExceedsLimit(new string('x', OrielIpcLimits.MaxInboundMessageChars + 1)));

    [Fact]
    public void OversizeReason_NamesActualAndMaximumLength()
    {
        int length = OrielIpcLimits.MaxInboundMessageChars + 1024;
        string reason = OrielIpcLimits.OversizeReason(length);

        Assert.Contains(length.ToString(System.Globalization.CultureInfo.InvariantCulture), reason, StringComparison.Ordinal);
        Assert.Contains(
            OrielIpcLimits.MaxInboundMessageChars.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason,
            StringComparison.Ordinal);
    }
}
