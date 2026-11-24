using System.Runtime.InteropServices;

namespace SplitHelper.Services;

public sealed record ProxySnapshot(uint Flags, string Server, string Bypass, string AutoConfigUrl)
{
    public bool SameAs(ProxySnapshot other) => Flags == other.Flags && string.Equals(Server, other.Server, StringComparison.OrdinalIgnoreCase) && Bypass == other.Bypass && AutoConfigUrl == other.AutoConfigUrl;
    public bool PointsTo(int? port)
    {
        if ((Flags & 2) == 0) return false;
        foreach (var entry in Server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = entry[(entry.IndexOf('=') + 1)..].Trim();
            if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value, UriKind.Absolute, out var address) || (port is not null && address.Port != port)) continue;
            var host = address.DnsSafeHost.TrimEnd('.');
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))) return true;
        }
        return false;
    }
}
public sealed record ProxyConnection(string? Name, ProxySnapshot Original, ProxySnapshot Applied);
public sealed record ProxySession(ProxySnapshot Original, ProxySnapshot Applied, int Port, ProxyConnection[]? Connections = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public ProxyConnection[] Entries => Connections ?? [new(null, Original, Applied)];
}

public sealed class WindowsProxy(AppPaths paths)
{
    public bool HasSession => File.Exists(paths.ProxySessionFile);
    public ProxySession? Session => SettingsStore.ReadProtected<ProxySession>(paths.ProxySessionFile);
    public bool IsActive
    {
        get
        {
            if (Session is not { } session) return false;
            var names = RasConnectionNames();
            return session.Entries.Where(e => e.Name is null || names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)).All(e => Read(e.Name).SameAs(e.Applied)) && EffectiveMatches(session.Port);
        }
    }
    public void Enable(int port, string? rasConnection = null)
    {
        var existing = Session;
        if (existing is not null && existing.Port != port) throw new UserError("请先还原旧代理接管记录，再更改分流端口。");
        var entries = existing?.Entries.ToList() ?? new List<ProxyConnection>();
        var acquired = new List<ProxyConnection>();
        var names = RasConnectionNames();
        if (rasConnection is not null && !names.Contains(rasConnection, StringComparer.OrdinalIgnoreCase)) throw new UserError("未找到 Windscribe 的拨号连接设置，请等待 IKEv2 连接完成。");
        foreach (var entry in entries.Where(e => e.Name is null || names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
            if (!Read(entry.Name).SameAs(entry.Applied)) throw new UserError("默认或 VPN 连接代理已被其他程序改变。请先停止并还原，再启用分流；助手不会自动覆盖外部设置。");
        foreach (var name in new string?[] { null, rasConnection }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (entries.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            var original = Read(name);
            if (original.PointsTo(port)) throw new UserError("当前代理已使用助手端口，但缺少对应还原记录，请先在 Windows 代理设置中处理后重试。");
            var entry = new ProxyConnection(name, original, new ProxySnapshot(3, $"127.0.0.1:{port}", original.Bypass, ""));
            entries.Add(entry); acquired.Add(entry);
        }
        var first = entries.Single(e => e.Name is null);
        if (existing?.Connections is null || entries.Count != existing.Entries.Length)
            SettingsStore.WriteProtected(paths.ProxySessionFile, new ProxySession(first.Original, first.Applied, port, entries.ToArray()));
        foreach (var entry in acquired)
        {
            if (!Read(entry.Name).SameAs(entry.Original)) throw new UserError("接管前的代理设置发生变化，已保留备份且未覆盖外部设置，请先停止并还原后重试。");
            Write(entry.Applied, entry.Name);
        }
        foreach (var entry in entries.Where(e => e.Name is null || names.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
        {
            if (!Read(entry.Name).SameAs(entry.Applied)) throw new UserError("Windows 未保持默认或 VPN 代理设置；还原记录已保留。");
        }
        if (!EffectiveMatches(port)) throw new UserError("当前活动连接未使用助手代理，请检查其他 VPN 或代理软件。");
    }
    public bool Restore()
    {
        var session = Session;
        if (session is null) return true;
        var external = false; var failed = false;
        foreach (var entry in session.Entries)
        {
            try
            {
                // WinINet stores named proxy settings separately from the RAS phonebook.
                // Windscribe can remove the phonebook entry while its proxy settings still exist.
                var current = Read(entry.Name);
                if (current.SameAs(entry.Applied)) { Write(entry.Original, entry.Name); if (!Read(entry.Name).SameAs(entry.Original)) failed = true; }
                else if (!current.SameAs(entry.Original)) external = true;
            }
            catch (System.ComponentModel.Win32Exception) { failed = true; }
        }
        if (failed) throw new UserError("部分连接代理未能还原，组件与逐连接备份已保留，请重试停止。");
        if (external || ReferencesAny(session.Port)) return false;
        File.Delete(paths.ProxySessionFile);
        return true;
    }
    public void ArchiveExternalSession()
    {
        var session = Session;
        if (session is null) return;
        if (ReferencesAny(session.Port)) throw new UserError("默认或 VPN 连接代理仍引用本助手端口，组件与还原记录已保留。");
        var directory = Path.Combine(paths.DataDirectory, "proxy-history");
        Directory.CreateDirectory(directory);
        File.Move(paths.ProxySessionFile, Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + ".dat"));
    }
    public bool ReferencesAny(params int[] ports)
    {
        var savedNames = Session?.Entries.Select(entry => entry.Name) ?? [];
        return new string?[] { null }.Concat(RasConnectionNames()).Concat(savedNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(Read).Append(ReadEffective()).Any(snapshot => ports.Any(port => snapshot.PointsTo(port)));
    }
    public bool ReferencesLocalProxy() => new string?[] { null }.Concat(RasConnectionNames()).Select(Read).Append(ReadEffective()).Any(snapshot => snapshot.PointsTo(null));
    public static bool EffectiveMatches(int port)
    {
        var effective = ReadEffective();
        return effective.Flags == 3 && string.Equals(effective.Server, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(effective.AutoConfigUrl);
    }
    public static ProxySnapshot Read(string? connection = null)
    {
        try { return Query(10, connection); }
        catch (System.ComponentModel.Win32Exception) { return Query(1, connection); }
    }
    private static ProxySnapshot Query(uint flagsOption, string? connection)
    {
        var size = Marshal.SizeOf<NativeOption>();
        var buffer = Marshal.AllocHGlobal(size * 4);
        var ids = new uint[] { flagsOption, 2, 3, 4 };
        var name = connection is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
        try
        {
            for (var i = 0; i < 4; i++) Marshal.StructureToPtr(new NativeOption { Id = ids[i] }, buffer + i * size, false);
            var list = NewList(buffer); list.Connection = name; var bytes = (uint)Marshal.SizeOf<NativeList>();
            if (!InternetQueryOption(IntPtr.Zero, 75, ref list, ref bytes)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var options = Enumerable.Range(0, 4).Select(i => Marshal.PtrToStructure<NativeOption>(buffer + i * size)).ToArray();
            return new ProxySnapshot(options[0].Value.Number, Text(options[1].Value.Pointer), Text(options[2].Value.Pointer), Text(options[3].Value.Pointer));
        }
        finally
        {
            for (var i = 1; i < 4; i++) { var option = Marshal.PtrToStructure<NativeOption>(buffer + i * size); if (option.Value.Pointer != IntPtr.Zero) GlobalFree(option.Value.Pointer); }
            Marshal.FreeHGlobal(buffer);
            if (name != IntPtr.Zero) Marshal.FreeHGlobal(name);
        }
    }
    private static void Write(ProxySnapshot snapshot, string? connection = null)
    {
        var strings = new[] { Marshal.StringToHGlobalUni(snapshot.Server), Marshal.StringToHGlobalUni(snapshot.Bypass), Marshal.StringToHGlobalUni(snapshot.AutoConfigUrl) };
        var size = Marshal.SizeOf<NativeOption>(); var buffer = Marshal.AllocHGlobal(size * 4);
        var name = connection is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(connection);
        try
        {
            Marshal.StructureToPtr(new NativeOption { Id = 1, Value = new NativeValue { Number = snapshot.Flags } }, buffer, false);
            for (var i = 1; i < 4; i++) Marshal.StructureToPtr(new NativeOption { Id = (uint)i + 1, Value = new NativeValue { Pointer = strings[i - 1] } }, buffer + i * size, false);
            var list = NewList(buffer);
            list.Connection = name;
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, (uint)Marshal.SizeOf<NativeList>())) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            InternetNotify(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetNotify(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        finally { Marshal.FreeHGlobal(buffer); foreach (var pointer in strings) Marshal.FreeHGlobal(pointer); if (name != IntPtr.Zero) Marshal.FreeHGlobal(name); }
    }
    public static ProxySnapshot ReadEffective()
    {
        if (!WinHttpGetIEProxyConfigForCurrentUser(out var config)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return new ProxySnapshot((uint)(1 | (config.Proxy != IntPtr.Zero ? 2 : 0) | (config.AutoConfigUrl != IntPtr.Zero ? 4 : 0) | (config.AutoDetect ? 8 : 0)), Text(config.Proxy), Text(config.Bypass), Text(config.AutoConfigUrl)); }
        finally { foreach (var p in new[] { config.Proxy, config.Bypass, config.AutoConfigUrl }) if (p != IntPtr.Zero) GlobalFree(p); }
    }
    public static string[] RasConnectionNames()
    {
        uint bytes = 0; var error = RasEnumEntries(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref bytes, out var count);
        if (error == 0 && count == 0) return [];
        if (error != 603 && error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        var size = Marshal.SizeOf<RasEntry>(); var buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            Marshal.WriteInt32(buffer, size);
            error = RasEnumEntries(IntPtr.Zero, IntPtr.Zero, buffer, ref bytes, out count);
            if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
            return Enumerable.Range(0, (int)count).Select(i => Marshal.PtrToStructure<RasEntry>(buffer + i * size).Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string Text(IntPtr pointer) => pointer == IntPtr.Zero ? "" : Marshal.PtrToStringUni(pointer) ?? "";
    private static NativeList NewList(IntPtr options) => new() { Size = (uint)Marshal.SizeOf<NativeList>(), Count = 4, Options = options };
    [StructLayout(LayoutKind.Explicit)] private struct NativeValue { [FieldOffset(0)] public uint Number; [FieldOffset(0)] public IntPtr Pointer; [FieldOffset(0)] public long FileTime; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeOption { public uint Id; public NativeValue Value; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeList { public uint Size; public IntPtr Connection; public uint Count; public uint Error; public IntPtr Options; }
    [StructLayout(LayoutKind.Sequential)] private struct IeConfig { [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect; public IntPtr AutoConfigUrl; public IntPtr Proxy; public IntPtr Bypass; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct RasEntry { public uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Name; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)] public string Phonebook; }
    [DllImport("rasapi32.dll", EntryPoint = "RasEnumEntriesW", CharSet = CharSet.Unicode)] private static extern uint RasEnumEntries(IntPtr reserved, IntPtr phonebook, IntPtr entries, ref uint bytes, out uint count);
    [DllImport("winhttp.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out IeConfig config);
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetQueryOption(IntPtr handle, uint option, ref NativeList buffer, ref uint length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetSetOption(IntPtr handle, uint option, ref NativeList buffer, uint length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetNotify(IntPtr handle, uint option, IntPtr buffer, uint length);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr pointer);
}
