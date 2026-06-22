using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Zephyr.UI.Services;

public record WpdItem(string ObjectId, string Name, bool IsFolder, long Size, DateTime DateModified);

public static class WpdProvider
{
    public  const string PathPrefix         = "wpd:";
    private const char   PathSep            = '|';
    public  const string DeviceRootObjectId = "DEVICE";

    // Resolved display names, populated as folders are enumerated. Lets the tab header /
    // breadcrumb show "SD" instead of the raw object ID without re-querying the device.
    private static readonly ConcurrentDictionary<string, string> _nameCache = new();
    private static string NameCacheKey(string deviceId, string objectId) => deviceId + PathSep + objectId;

    /// <summary>Returns a previously-enumerated display name for a WPD object, or null if unknown.</summary>
    public static string? GetCachedName(string deviceId, string objectId) =>
        _nameCache.TryGetValue(NameCacheKey(deviceId, objectId), out var name) ? name : null;

    // WPD_OBJECT_PROPERTIES_V1 fmtid = {EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C}
    private static readonly Guid _objectFmtid    = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    // WPD_STORAGE_OBJECT_PROPERTIES_V1 fmtid — pid=7 is WPD_STORAGE_DESCRIPTION
    private static readonly Guid _storageFmtid   = new("01A3057A-74D6-4E80-BEA7-DC4C212CE50A");
    private static readonly Guid _clientInfoFmtid = new("204D9F0C-2292-4080-9F42-40664E70F859");
    private static readonly Guid _folderType      = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    private static readonly Guid _functionalType  = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");
    // WPD_RESOURCE_DEFAULT — the object's primary data stream (pid 0)
    private static readonly Guid _resourceDefaultFmtid = new("E81E79BE-34F0-41BF-B53F-F1A06AE87842");
    // WPD_RESOURCE_THUMBNAIL — small preview image (pid 0)
    private static readonly Guid _resourceThumbFmtid   = new("C7C407BA-98FA-46B5-9960-23FEC124CFDE");

    private static readonly Guid _clsidManager       = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    private static readonly Guid _clsidDeviceFtm     = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    private static readonly Guid _clsidValues        = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    private static readonly Guid _clsidKeyCollection = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");

    // ── Path helpers ──────────────────────────────────────────────────────────

    public static bool IsWpdPath(string? path) =>
        path != null && path.StartsWith(PathPrefix, StringComparison.Ordinal);

    public static string MakePath(string deviceId, string objectId) =>
        PathPrefix + deviceId + PathSep + objectId;

    public static string MakeRootPath(string deviceId) =>
        MakePath(deviceId, DeviceRootObjectId);

    public static (string DeviceId, string ObjectId) ParsePath(string path)
    {
        var rest = path[PathPrefix.Length..];
        var sep  = rest.IndexOf(PathSep);
        return sep < 0 ? (rest, DeviceRootObjectId) : (rest[..sep], rest[(sep + 1)..]);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static IReadOnlyList<(string DeviceId, string FriendlyName)> GetDevices()
    {
        try
        {
            var mgr    = CreateCom<IPortableDeviceManager>(_clsidManager);
            var ids    = EnumDeviceIds(mgr);
            var result = ids.Select(id => (id, GetFriendlyName(mgr, id))).ToList();
            Marshal.ReleaseComObject(mgr);
            return result;
        }
        catch { return []; }
    }

    public static IReadOnlyList<WpdItem> GetChildren(string deviceId, string objectId)
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine($"GetChildren deviceId={deviceId} objectId={objectId}");

        IPortableDevice? device = null;
        try
        {
            device = OpenDevice(deviceId, log);
            if (device == null) { WriteLog(log); return []; }

            IPortableDeviceContent? content = null;
            try { device.Content(out content); log.AppendLine("Content() ok"); }
            catch (Exception ex) { log.AppendLine($"Content() threw: 0x{ex.HResult:X8} {ex.Message}"); WriteLog(log); return []; }

            if (content == null) { log.AppendLine("content null"); WriteLog(log); return []; }

            IPortableDeviceProperties? props = null;
            try { content.Properties(out props); log.AppendLine("Properties() ok"); }
            catch (Exception ex) { log.AppendLine($"Properties() threw: 0x{ex.HResult:X8} {ex.Message}"); }

            IEnumPortableDeviceObjectIDs? enumerator = null;
            try
            {
                // Pass IntPtr.Zero for pFilter — avoids any nullable-interface marshaling issue
                int enumHr = content.EnumObjects(0, objectId, IntPtr.Zero, out enumerator);
                log.AppendLine($"EnumObjects() hr=0x{enumHr:X8} enumerator={enumerator != null}");
            }
            catch (Exception ex)
            {
                log.AppendLine($"EnumObjects() threw: 0x{ex.HResult:X8} {ex.Message}");
                WriteLog(log); return [];
            }

            if (enumerator == null) { log.AppendLine("enumerator null"); WriteLog(log); return []; }

            var nameKey   = new PROPERTYKEY(_objectFmtid, 4);   // WPD_OBJECT_NAME
            var fnameKey  = new PROPERTYKEY(_objectFmtid, 12);  // WPD_OBJECT_ORIGINAL_FILE_NAME
            var typeKey   = new PROPERTYKEY(_objectFmtid, 7);   // WPD_OBJECT_CONTENT_TYPE
            var sizeKey   = new PROPERTYKEY(_objectFmtid, 11);  // WPD_OBJECT_SIZE
            var dateKey   = new PROPERTYKEY(_objectFmtid, 19);  // WPD_OBJECT_DATE_MODIFIED
            var storDescKey = new PROPERTYKEY(_storageFmtid, 7); // WPD_STORAGE_DESCRIPTION

            IPortableDeviceKeyCollection? keys = null;
            if (props != null)
                try { keys = BuildKeys(nameKey, fnameKey, typeKey, sizeKey, dateKey, storDescKey); }
                catch (Exception ex) { log.AppendLine($"BuildKeys() threw: {ex.Message}"); }

            var results = new List<WpdItem>();
            const int Batch = 32;
            var buf = Marshal.AllocCoTaskMem(Batch * IntPtr.Size);
            try
            {
                while (true)
                {
                    int hr = enumerator.Next((uint)Batch, buf, out uint fetched);
                    log.AppendLine($"Next() hr=0x{hr:X8} fetched={fetched}");

                    for (uint i = 0; i < fetched; i++)
                    {
                        var ptr = Marshal.ReadIntPtr(buf, (int)(i * IntPtr.Size));
                        if (ptr == IntPtr.Zero) continue;
                        var childId = Marshal.PtrToStringUni(ptr) ?? string.Empty;
                        Marshal.FreeCoTaskMem(ptr);
                        if (string.IsNullOrEmpty(childId)) continue;

                        log.AppendLine($"  child: {childId}");

                        if (props != null)
                        {
                            try
                            {
                                IPortableDeviceValues? vals = null;
                                try { props.GetValues(childId, keys, out vals); }
                                catch
                                {
                                    log.AppendLine("    GetValues(keys) failed, retrying with null keys");
                                    props.GetValues(childId, null, out vals);
                                }
                                log.AppendLine($"    vals={vals != null} keys={keys != null}");
                                var isFolder    = IsFolder(vals!, ref typeKey);
                                // For storage/functional objects also try WPD_STORAGE_DESCRIPTION
                                var displayName = isFolder
                                    ? (SafeStrLog(vals, ref nameKey, "name", log)
                                       ?? SafeStrLog(vals, ref fnameKey, "fname", log)
                                       ?? SafeStrLog(vals, ref storDescKey, "storDesc", log)
                                       ?? childId)
                                    : (SafeStrLog(vals, ref fnameKey, "fname", log)
                                       ?? SafeStrLog(vals, ref nameKey, "name", log)
                                       ?? childId);
                                var size = isFolder ? 0L : SafeULong(vals, ref sizeKey);
                                var date = SafeDate(vals, ref dateKey);
                                log.AppendLine($"    => name={displayName} isFolder={isFolder}");
                                if (displayName != childId)
                                    _nameCache[NameCacheKey(deviceId, childId)] = displayName;
                                results.Add(new WpdItem(childId, displayName, isFolder, size, date));
                                if (vals != null) Marshal.ReleaseComObject(vals);
                            }
                            catch (Exception ex)
                            {
                                log.AppendLine($"    GetValues threw: 0x{ex.HResult:X8} {ex.Message}");
                                results.Add(new WpdItem(childId, childId, true, 0, DateTime.MinValue));
                            }
                        }
                        else
                        {
                            results.Add(new WpdItem(childId, childId, true, 0, DateTime.MinValue));
                        }
                    }
                    if (fetched == 0 || hr != 0) break;
                }
            }
            finally { Marshal.FreeCoTaskMem(buf); }

            log.AppendLine($"Total results: {results.Count}");
            if (keys  != null) Marshal.ReleaseComObject(keys);
            Marshal.ReleaseComObject(enumerator);
            if (props != null) Marshal.ReleaseComObject(props);
            Marshal.ReleaseComObject(content);
            WriteLog(log);
            return results;
        }
        catch (Exception ex)
        {
            log.AppendLine($"Outer exception: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            WriteLog(log); return [];
        }
        finally { TryRelease(device); }
    }

    public static string GetParentObjectId(string deviceId, string objectId)
    {
        if (objectId == DeviceRootObjectId) return DeviceRootObjectId;
        IPortableDevice? device = null;
        try
        {
            device = OpenDevice(deviceId);
            if (device == null) return DeviceRootObjectId;
            device.Content(out var content);
            content.Properties(out var props);
            var key  = new PROPERTYKEY(_objectFmtid, 3); // WPD_OBJECT_PARENT_ID
            var keys = BuildKeys(key);
            props.GetValues(objectId, keys, out var vals);
            var parentId = SafeStr(vals, ref key) ?? DeviceRootObjectId;
            Marshal.ReleaseComObject(vals);
            Marshal.ReleaseComObject(keys);
            Marshal.ReleaseComObject(props);
            Marshal.ReleaseComObject(content);
            return parentId;
        }
        catch { return DeviceRootObjectId; }
        finally { TryRelease(device); }
    }

    public static string GetDisplayName(string deviceId, string objectId)
    {
        if (objectId == DeviceRootObjectId)
        {
            var match = GetDevices().FirstOrDefault(d => d.DeviceId == deviceId);
            return string.IsNullOrEmpty(match.FriendlyName) ? deviceId : match.FriendlyName;
        }
        IPortableDevice? device = null;
        try
        {
            device = OpenDevice(deviceId);
            if (device == null) return objectId;
            device.Content(out var content);
            content.Properties(out var props);
            var nameKey  = new PROPERTYKEY(_objectFmtid, 4);
            var fnameKey = new PROPERTYKEY(_objectFmtid, 12);
            var keys     = BuildKeys(nameKey, fnameKey);
            props.GetValues(objectId, keys, out var vals);
            var name = SafeStr(vals, ref nameKey) ?? SafeStr(vals, ref fnameKey) ?? objectId;
            Marshal.ReleaseComObject(vals);
            Marshal.ReleaseComObject(keys);
            Marshal.ReleaseComObject(props);
            Marshal.ReleaseComObject(content);
            return name;
        }
        catch { return objectId; }
        finally { TryRelease(device); }
    }

    /// <summary>
    /// Streams a WPD object's default data resource to a temp file and returns its path,
    /// or null on failure. Used to open/preview camera files that don't exist on disk.
    /// </summary>
    public static string? CopyToTempFile(string deviceId, string objectId, string fileName)
    {
        var bytes = ReadResource(deviceId, objectId, new PROPERTYKEY(_resourceDefaultFmtid, 0));
        if (bytes == null) return null;
        try
        {
            var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ZephyrWpd");
            System.IO.Directory.CreateDirectory(tempDir);
            var safeName = string.Join("_", fileName.Split(System.IO.Path.GetInvalidFileNameChars()));
            var tempPath = System.IO.Path.Combine(tempDir, safeName);
            System.IO.File.WriteAllBytes(tempPath, bytes);
            return tempPath;
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads a small preview for a WPD image object: the dedicated thumbnail resource if the
    /// device exposes one, otherwise the full image. Returns encoded image bytes, or null.
    /// </summary>
    public static byte[]? ReadThumbnailBytes(string deviceId, string objectId) =>
        ReadResource(deviceId, objectId, new PROPERTYKEY(_resourceThumbFmtid, 0))
        ?? ReadResource(deviceId, objectId, new PROPERTYKEY(_resourceDefaultFmtid, 0));

    // PTP cameras typically allow only one active session, but thumbnails load in parallel —
    // serialize resource reads so we never open concurrent device sessions.
    private static readonly object _readLock = new();

    private static byte[]? ReadResource(string deviceId, string objectId, PROPERTYKEY resourceKey)
    {
        lock (_readLock)
        {
        IPortableDevice? device = null;
        System.Runtime.InteropServices.ComTypes.IStream? stream = null;
        try
        {
            device = OpenDevice(deviceId);
            if (device == null) return null;
            device.Content(out var content);
            content.Transfer(out var resources);

            var key = resourceKey;
            uint optimal = 0;
            const uint STGM_READ = 0;
            int hr = resources.GetStream(objectId, ref key, STGM_READ, ref optimal, out stream);
            if (hr < 0 || stream == null) return null;
            if (optimal == 0) optimal = 128 * 1024;

            using var ms = new System.IO.MemoryStream();
            var pcbRead = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                var buffer = new byte[optimal];
                while (true)
                {
                    stream.Read(buffer, buffer.Length, pcbRead);
                    int read = Marshal.ReadInt32(pcbRead);
                    if (read <= 0) break;
                    ms.Write(buffer, 0, read);
                }
            }
            finally { Marshal.FreeCoTaskMem(pcbRead); }

            Marshal.ReleaseComObject(resources);
            Marshal.ReleaseComObject(content);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch { return null; }
        finally
        {
            if (stream != null) Marshal.ReleaseComObject(stream);
            TryRelease(device);
        }
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static T CreateCom<T>(Guid clsid) where T : class
    {
        var t = Type.GetTypeFromCLSID(clsid)
            ?? throw new InvalidOperationException($"COM class {clsid} not registered");
        return (T)Activator.CreateInstance(t)!;
    }

    private static string[] EnumDeviceIds(IPortableDeviceManager mgr)
    {
        uint count = 0;
        mgr.GetDevices(IntPtr.Zero, ref count);
        if (count == 0) return [];

        var buf = Marshal.AllocCoTaskMem((int)(count * IntPtr.Size));
        try
        {
            mgr.GetDevices(buf, ref count);
            var ids = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(buf, (int)(i * IntPtr.Size));
                if (ptr != IntPtr.Zero)
                    ids.Add(Marshal.PtrToStringUni(ptr) ?? string.Empty);
            }
            return ids.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }
        finally { Marshal.FreeCoTaskMem(buf); }
    }

    private static string GetFriendlyName(IPortableDeviceManager mgr, string id)
    {
        try
        {
            uint len = 0;
            mgr.GetDeviceFriendlyName(id, null, ref len);
            if (len == 0) return id;
            var buf = new char[len];
            mgr.GetDeviceFriendlyName(id, buf, ref len);
            return new string(buf).TrimEnd('\0');
        }
        catch { return id; }
    }

    private static IPortableDevice? OpenDevice(string deviceId, System.Text.StringBuilder? log = null)
    {
        IPortableDevice? device = null;
        IPortableDeviceValues? info = null;
        try
        {
            log?.AppendLine("  CreateCom<IPortableDevice>...");
            device = CreateCom<IPortableDevice>(_clsidDeviceFtm);
            log?.AppendLine($"  device ok: {device != null}");

            log?.AppendLine("  CreateCom<IPortableDeviceValues>...");
            info = CreateCom<IPortableDeviceValues>(_clsidValues);
            log?.AppendLine($"  info ok: {info != null}");

            // Try to populate client info; if any Set call fails, open with empty info
            try
            {
                var kName   = new PROPERTYKEY(_clientInfoFmtid, 2);
                log?.AppendLine("  SetStringValue(Name)...");
                info.SetStringValue(ref kName, "Zephyr");
                log?.AppendLine("  ok");

                var kMaj    = new PROPERTYKEY(_clientInfoFmtid, 3);
                log?.AppendLine("  SetUnsignedIntegerValue(Major)...");
                info.SetUnsignedIntegerValue(ref kMaj, 1);
                log?.AppendLine("  ok");

                var kMin    = new PROPERTYKEY(_clientInfoFmtid, 4);
                info.SetUnsignedIntegerValue(ref kMin, 0);

                var kRev    = new PROPERTYKEY(_clientInfoFmtid, 5);
                info.SetUnsignedIntegerValue(ref kRev, 0);

                var kAccess = new PROPERTYKEY(_clientInfoFmtid, 7);
                info.SetUnsignedIntegerValue(ref kAccess, 0x80000000u);

                log?.AppendLine("  all Set calls ok");
            }
            catch (Exception ex)
            {
                log?.AppendLine($"  Set call threw: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                log?.AppendLine("  proceeding with empty clientInfo anyway");
            }

            log?.AppendLine("  Calling Open...");
            device.Open(deviceId, info);
            log?.AppendLine("  Open() succeeded");
            Marshal.ReleaseComObject(info);
            return device;
        }
        catch (Exception ex)
        {
            log?.AppendLine($"  OpenDevice threw: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            TryRelease(info);
            TryRelease(device);
            return null;
        }
    }

    private static void WriteLog(System.Text.StringBuilder log)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zephyr_wpd.log");
            System.IO.File.AppendAllText(path, log.ToString() + "---\n");
        }
        catch { }
    }

    private static IPortableDeviceKeyCollection BuildKeys(params PROPERTYKEY[] keys)
    {
        var col = CreateCom<IPortableDeviceKeyCollection>(_clsidKeyCollection);
        foreach (var k in keys) { var kk = k; col.Add(ref kk); }
        return col;
    }

    private static void TryRelease(object? obj) { if (obj != null) try { Marshal.ReleaseComObject(obj); } catch { } }

    private static string? SafeStr(IPortableDeviceValues v, ref PROPERTYKEY k)
    {
        int hr = v.GetStringValue(ref k, out var s);
        return hr >= 0 && !string.IsNullOrEmpty(s) ? s : null;
    }

    private static string? SafeStrLog(IPortableDeviceValues v, ref PROPERTYKEY k, string label, System.Text.StringBuilder log)
    {
        int hr = v.GetStringValue(ref k, out var s);
        log.AppendLine(hr >= 0 ? $"    {label}={s ?? "(null)"}" : $"    {label} hr=0x{hr:X8}");
        return hr >= 0 && !string.IsNullOrEmpty(s) ? s : null;
    }

    private static long SafeULong(IPortableDeviceValues v, ref PROPERTYKEY k)
    { try { v.GetUnsignedLargeIntegerValue(ref k, out var n); return (long)n; } catch { return 0; } }

    private static DateTime SafeDate(IPortableDeviceValues v, ref PROPERTYKEY k)
    {
        int hr = v.GetStringValue(ref k, out var s);
        if (hr >= 0 && s != null &&
            DateTime.TryParseExact(s, "yyyy/MM/dd:HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return dt;
        return DateTime.MinValue;
    }

    private static bool IsFolder(IPortableDeviceValues v, ref PROPERTYKEY k)
    {
        try { v.GetGuidValue(ref k, out var g); return g == _folderType || g == _functionalType; }
        catch { return true; } // unknown type → treat as folder so it's navigable
    }

    // ── COM interface declarations ─────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid   = pid;
    }

    [ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices(IntPtr pPnPDeviceIDs, ref uint pcPnPDeviceIDs);
        void RefreshDeviceList();
        [PreserveSig] int GetDeviceFriendlyName(
            [MarshalAs(UnmanagedType.LPWStr)] string id,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2, SizeParamIndex = 2)] char[]? buf,
            ref uint len);
    }

    [ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDevice
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IPortableDeviceValues pClientInfo);
        void SendCommand(uint dwFlags, IPortableDeviceValues pParameters, out IPortableDeviceValues ppResults);
        void Content(out IPortableDeviceContent ppContent);
    }

    [ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceContent
    {
        // pFilter is IntPtr so null marshals as a clean null COM pointer
        [PreserveSig] int EnumObjects(
            uint dwFlags,
            [MarshalAs(UnmanagedType.LPWStr)] string pszParentObjectID,
            IntPtr pFilter,
            out IEnumPortableDeviceObjectIDs ppEnum);
        void Properties(out IPortableDeviceProperties ppProperties);   // vtable slot 2
        void Transfer(out IPortableDeviceResources ppResources);       // vtable slot 3
    }

    // GetStream is vtable slot 3 — GetSupportedResources/GetResourceAttributes must precede it.
    [ComImport, Guid("FD8878AC-D841-4D17-891C-E6829CDB6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceResources
    {
        void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection ppKeys);
        void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PROPERTYKEY key, out IPortableDeviceValues ppResourceAttributes);
        [PreserveSig] int GetStream(
            [MarshalAs(UnmanagedType.LPWStr)] string objectId,
            ref PROPERTYKEY key,
            uint dwMode,
            ref uint pdwOptimalBufferSize,
            out System.Runtime.InteropServices.ComTypes.IStream ppStream);
    }

    [ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumPortableDeviceObjectIDs
    {
        [PreserveSig] int Next(uint cObjects, IntPtr pObjIDs, out uint pcFetched);
    }

    [ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceProperties
    {
        void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IPortableDeviceKeyCollection ppKeys);
        void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PROPERTYKEY key, out IPortableDeviceValues ppAttribs);
        void GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection? pKeys, out IPortableDeviceValues ppValues);
    }

    // Vtable order MUST match PortableDeviceApi.h exactly. SetValue/GetValue (the generic
    // PROPVARIANT accessors) come right after GetAt and before SetStringValue — omitting them
    // shifts every later method down two slots, so SetStringValue actually calls SetValue
    // (DISP_E_BADVARTYPE) and GetStringValue actually calls GetValue (E_POINTER).
    // Every PROPERTYKEY parameter is ref — COM uses REFPROPERTYKEY (const PROPERTYKEY *), a pointer.
    [ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceValues
    {
        void GetCount(out uint pcelt);
        void GetAt(uint index, IntPtr pKey, IntPtr pValue);
        void SetValue(ref PROPERTYKEY key, IntPtr pValue);  // const PROPVARIANT* — unused
        void GetValue(ref PROPERTYKEY key, IntPtr pValue);  // PROPVARIANT* — unused
        void SetStringValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        // [PreserveSig] returns raw HRESULT — avoids vtable null-slot crash and lets us handle E_POINTER safely
        [PreserveSig] int GetStringValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string pValue);
        void SetUnsignedIntegerValue(ref PROPERTYKEY key, uint value);
        void GetUnsignedIntegerValue(ref PROPERTYKEY key, out uint pValue);
        void SetSignedIntegerValue(ref PROPERTYKEY key, int value);
        void GetSignedIntegerValue(ref PROPERTYKEY key, out int pValue);
        void SetUnsignedLargeIntegerValue(ref PROPERTYKEY key, ulong value);
        void GetUnsignedLargeIntegerValue(ref PROPERTYKEY key, out ulong pValue);
        void SetSignedLargeIntegerValue(ref PROPERTYKEY key, long value);
        void GetSignedLargeIntegerValue(ref PROPERTYKEY key, out long pValue);
        void SetFloatValue(ref PROPERTYKEY key, float value);
        void GetFloatValue(ref PROPERTYKEY key, out float pValue);
        void SetErrorValue(ref PROPERTYKEY key, int value);
        void GetErrorValue(ref PROPERTYKEY key, out int pValue);
        void SetKeyValue(ref PROPERTYKEY key, ref PROPERTYKEY value);
        void GetKeyValue(ref PROPERTYKEY key, out PROPERTYKEY pValue);
        void SetBoolValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] bool value);
        void GetBoolValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.Bool)] out bool pValue);
        void SetIUnknownValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void GetIUnknownValue(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.IUnknown)] out object pValue);
        void SetGuidValue(ref PROPERTYKEY key, ref Guid value);
        void GetGuidValue(ref PROPERTYKEY key, out Guid pValue);
    }

    [ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPortableDeviceKeyCollection
    {
        void GetCount(out uint pcElems);
        void GetAt(uint dwIndex, out PROPERTYKEY pKey);
        void Add(ref PROPERTYKEY key);
    }
}
