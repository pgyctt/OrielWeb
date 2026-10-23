using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using OrielWeb.Ipc;
using OrielWeb.Platform.Linux.Interop;
namespace OrielWeb.Platform.Linux;

internal static class LinuxMappings
{

    /// <summary>公共的 resize 方向 → GTK 的窗口边缘。</summary>
    internal static GdkWindowEdge EdgeFor(OrielResizeDirection direction) => direction switch
    {
        OrielResizeDirection.East => GdkWindowEdge.East,
        OrielResizeDirection.North => GdkWindowEdge.North,
        OrielResizeDirection.NorthEast => GdkWindowEdge.NorthEast,
        OrielResizeDirection.NorthWest => GdkWindowEdge.NorthWest,
        OrielResizeDirection.South => GdkWindowEdge.South,
        OrielResizeDirection.SouthEast => GdkWindowEdge.SouthEast,
        OrielResizeDirection.SouthWest => GdkWindowEdge.SouthWest,
        _ => GdkWindowEdge.West,
    };
}
