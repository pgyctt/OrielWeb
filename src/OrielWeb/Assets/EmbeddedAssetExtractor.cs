using System.Reflection;

namespace OrielWeb;

/// <summary>
/// 把 csproj 中 EmbeddedResource 的 wwwroot 资源解压到用户目录，
/// 供平台后端以虚拟主机（如 https://app.oriel/）提供。
/// </summary>
internal static class EmbeddedAssetExtractor
{
    public static string Extract(string? resourcePrefixOverride)
    {
        var assembly = Assembly.GetEntryAssembly()
            ?? throw new InvalidOperationException("无法确定入口程序集。");

        var prefix = resourcePrefixOverride ?? $"{assembly.GetName().Name}.wwwroot.";
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrielWeb",
            assembly.GetName().Name ?? "app",
            "www");

        var names = assembly.GetManifestResourceNames();
        int count = 0;
        foreach (var name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // 资源名 → 相对路径：最后一个 '.' 之前是目录路径（'.'→分隔符），
            // 之后是扩展名，与文件名主干重新拼接。
            // 例：OrielDemo.wwwroot.app.js → app.js；…wwwroot.index.html → index.html
            // （文件名主干中再含 '.' 的资源不受支持）
            var relative = MapResourceToPath(name[prefix.Length..]);
            var target = Path.Combine(root, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            using var input = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"内嵌资源缺失：{name}");
            using var output = File.Create(target);
            input.CopyTo(output);
            count++;
        }

        if (count == 0)
        {
            throw new InvalidOperationException(
                $"未找到前缀为 '{prefix}' 的内嵌资源：请在 csproj 配置 <EmbeddedResource Include=\"wwwroot\\**\\*\" />，" +
                "或为 UseEmbeddedAssets 显式指定 resourcePrefix。");
        }

        return root;
    }

    private static string MapResourceToPath(string resourceSuffix)
    {
        var extensionIndex = resourceSuffix.LastIndexOf('.');
        if (extensionIndex < 0)
        {
            return resourceSuffix.Replace('.', Path.DirectorySeparatorChar);
        }
        var stem = resourceSuffix[..extensionIndex].Replace('.', Path.DirectorySeparatorChar);
        return stem + resourceSuffix[extensionIndex..];
    }
}
