using System.Buffers;
using System.Text.Json;
using OrielWeb.Ipc;

namespace OrielWeb;

/// <summary>
/// 数据存储标识符登记表（对标 Tauri 的 <c>fetch_data_store_identifiers</c> /
/// <c>remove_data_store</c> 背后的那张表）。
/// </summary>
/// <remarks>
/// <para>
/// <b>它登记的是什么</b>：macOS 的 <c>WKWebsiteDataStore</c> 支持"按 UUID 隔离一份
/// 浏览数据"（cookie / localStorage / 缓存各自一套）。本库把这个 UUID 开放成
/// <see cref="OrielWindowOptions.DataStoreIdentifier"/>，本表记录**本应用用过哪些 UUID**。
/// </para>
/// <para>
/// <b>为什么不用 WebKit 自己的枚举</b>：<c>+[WKWebsiteDataStore fetchAllDataStoreIdentifiers:]</c>
/// 是**异步回调**接口（要传一个 Objective-C block），而本库的 IPC 是同步请求-回执
/// ——在 UI 线程上等主队列的回调就是死锁。所以本库维护自己的表，并把它落在
/// 应用数据目录里（<c>AppData/data-stores.json</c>），这样**跨进程重启**也看得见
/// 上次创建过的存储——否则那些存储会永远留在磁盘上而没有任何入口能删掉它们。
/// </para>
/// <para>
/// <b>落盘的只是 UUID</b>，没有别的：数据本身在 WebKit 那边按 UUID 隔离存放。
/// 文件损坏时按空表处理并记诊断（不覆盖，等到下一次真正的增删才重写）。
/// </para>
/// </remarks>
internal sealed class OrielDataStoreRegistry
{
    /// <summary>登记文件名（在应用数据目录下）。</summary>
    internal const string FileName = "data-stores.json";

    private readonly object _gate = new();
    private readonly List<Guid> _ids = [];
    private readonly string? _filePath;
    private bool _loaded;

    /// <summary>显式指定落盘路径（<c>null</c> = 纯内存；测试用这个形态）。</summary>
    internal OrielDataStoreRegistry(string? filePath) => _filePath = filePath;

    /// <summary>按应用标识符定位落盘位置（取不到应用数据目录时退化为纯内存表）。</summary>
    internal static OrielDataStoreRegistry ForApp(string appIdentifier)
        => new(ResolveFilePath(appIdentifier));

    /// <summary>落盘路径；<c>null</c> 表示这份登记表只在内存里（进程结束即丢失）。</summary>
    internal string? FilePath => _filePath;

    /// <summary>当前登记的全部标识符（副本，按加入顺序）。</summary>
    internal IReadOnlyList<Guid> Identifiers
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _ids.ToArray();
            }
        }
    }

    /// <summary>登记一个标识符；已经存在时返回 <c>false</c>。</summary>
    internal bool Add(Guid identifier)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_ids.Contains(identifier))
            {
                return false;
            }

            _ids.Add(identifier);
            Save();
            return true;
        }
    }

    /// <summary>移除一个标识符；不存在时返回 <c>false</c>。</summary>
    internal bool Remove(Guid identifier)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_ids.Remove(identifier))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    /// <summary>某个标识符是否登记过。</summary>
    internal bool Contains(Guid identifier)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _ids.Contains(identifier);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Load();
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(_filePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("identifiers", out JsonElement list)
                || list.ValueKind != JsonValueKind.Array)
            {
                OrielDiagnostics.Report($"[OrielWeb] 数据存储登记表格式不对，按空表处理：{_filePath}");
                return;
            }

            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && Guid.TryParse(item.GetString(), out Guid identifier)
                    && !_ids.Contains(identifier))
                {
                    _ids.Add(identifier);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读失败不该让应用起不来：这份表只影响"能不能删掉旧存储"，
            // 空表最多让某个旧存储暂时看不见。
            OrielDiagnostics.Report($"[OrielWeb] 读取数据存储登记表失败（{_filePath}）：{ex.Message}");
        }
    }

    private void Save()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("identifiers");
                foreach (Guid identifier in _ids)
                {
                    writer.WriteStringValue(identifier.ToString("D"));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            // 先写临时文件再换名：写到一半掉电不会留下半个 JSON
            // （那种文件的症状是"下次启动读不出来，登记表像被清空了"）。
            string temporary = _filePath + ".tmp";
            File.WriteAllBytes(temporary, buffer.WrittenSpan.ToArray());
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去仍然保留内存里的表：本次运行照常工作，只是重启后看不到。
            OrielDiagnostics.Report($"[OrielWeb] 写入数据存储登记表失败（{_filePath}）：{ex.Message}");
        }
    }

    private static string? ResolveFilePath(string appIdentifier)
    {
        try
        {
            if (!OrielKnownDirs.IsSupported("AppData", appIdentifier))
            {
                return null;
            }

            return Path.Combine(OrielKnownDirs.Resolve("AppData", appIdentifier), FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OrielIpcException)
        {
            OrielDiagnostics.Report($"[OrielWeb] 无法定位应用数据目录，数据存储登记表只在内存里：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把标识符写成 16 字节（**大端 / RFC 4122 顺序**）。
    /// </summary>
    /// <remarks>
    /// 用 <c>ToString("N")</c> 再按十六进制解码：它给的就是 RFC 4122 的字节序，
    /// 而 <c>Guid.ToByteArray()</c> 前三段是小端（历史包袱）。页面侧拿到的是
    /// "UUID 的字节"，与 Tauri（Rust <c>Uuid::as_bytes</c>）一致才谈得上可迁移。
    /// </remarks>
    internal static byte[] ToBytes(Guid identifier) => Convert.FromHexString(identifier.ToString("N"));

    /// <summary>把 16 字节（大端 / RFC 4122 顺序）读回标识符。</summary>
    internal static bool TryFromBytes(ReadOnlySpan<byte> bytes, out Guid identifier)
    {
        if (bytes.Length != 16)
        {
            identifier = default;
            return false;
        }

        return Guid.TryParseExact(Convert.ToHexString(bytes), "N", out identifier);
    }
}
