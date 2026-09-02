using System.Runtime.InteropServices;

namespace Jane.Windows.Injection;

/// <param name="Data">The raw bytes of the HGLOBAL, exactly as the owning app supplied them.</param>
public sealed record ClipboardPayload(uint Format, byte[] Data);

/// <summary>Clipboard format ids and the names Jane registers.</summary>
public static class ClipboardFormats
{
    public const uint Text = 1;
    public const uint OemText = 7;
    public const uint UnicodeText = 13;
    public const uint Locale = 16;

    /// <summary>
    /// The formats Jane will save and put back.
    /// </summary>
    /// <remarks>
    /// Deliberately a short allowlist of memory-backed text formats rather than "everything on
    /// the clipboard". Two reasons. First, handle-backed formats (CF_BITMAP, CF_ENHMETAFILE)
    /// are GDI objects, and copying their bytes is meaningless. Second, and far more
    /// importantly, reading an arbitrary format can force a delayed render -- see
    /// <see cref="Win32Clipboard"/>. Restoration is best-effort by design: the plan says so,
    /// and the alternative is a strategy that can hang the app the user copied from.
    /// </remarks>
    public static IReadOnlyList<uint> RestorableStandardFormats { get; } = [UnicodeText, Text, OemText, Locale];

    /// <summary>Registered formats whose presence keeps content out of clipboard history and the cloud.</summary>
    /// <remarks>
    /// Windows' own clipboard history (Win+V) and cross-device sync honour these markers, so
    /// dictated text does not accumulate in a list the user forgot exists. Third-party clipboard
    /// managers are under no obligation to honour them, which is the caveat settings states.
    /// </remarks>
    public static IReadOnlyList<string> HistoryOptOutFormatNames { get; } =
    [
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard",
        "ExcludeClipboardContentFromMonitorProcessing",
    ];

    public const string HtmlFormatName = "HTML Format";
    public const string RtfFormatName = "Rich Text Format";
}

/// <summary>
/// Seam over the Win32 clipboard, so save-set-paste-restore can be asserted without a real one.
/// </summary>
/// <remarks>
/// The clipboard is global, single-instance machine state that belongs to the user. A test that
/// exercised the real one would silently destroy whatever the developer had copied, so every
/// clipboard test drives a fake through this interface instead.
/// </remarks>
public interface IClipboard
{
    /// <summary>Resolves a named format to its id, registering it if this is the first ask.</summary>
    uint RegisterFormat(string name);

    /// <summary>
    /// The format ids currently on the clipboard.
    /// </summary>
    /// <remarks>
    /// Listing ids is safe: it is <see cref="TryGetFormatData"/> that can force a render.
    /// </remarks>
    IReadOnlyList<uint> GetAvailableFormats();

    /// <summary>Raw bytes for one format, or null when it cannot be read.</summary>
    byte[]? TryGetFormatData(uint format);

    /// <summary>Empties the clipboard and writes exactly these payloads. An empty list clears it.</summary>
    void SetContents(IReadOnlyList<ClipboardPayload> payloads);
}

/// <summary>
/// The real Win32 clipboard.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Delayed rendering.</strong> An app may publish a format with a null handle, promising
/// to produce the data only if someone asks. Asking -- calling <c>GetClipboardData</c> -- sends
/// WM_RENDERFORMAT to that app and blocks until it answers, which for a large Excel selection
/// or a busy Electron app means an arbitrary stall, and for a wedged one means a hang that takes
/// Jane down with it. Jane therefore never walks the enumerated format list calling
/// <c>GetClipboardData</c>; it reads only the small allowlist in
/// <see cref="ClipboardFormats.RestorableStandardFormats"/> plus HTML and RTF, which are
/// memory-backed in practice. Formats outside that list are noted and lost, which is the
/// best-effort restoration the plan asks for.
/// </para>
/// <para>
/// <strong>Ownership.</strong> The clipboard is a global lock. Another app can hold it, so
/// opening it is retried briefly rather than treated as fatal on the first refusal. Each
/// operation opens and closes its own session rather than one session spanning the whole
/// save-set-paste-restore sequence, because the target application has to be able to open the
/// clipboard itself to service the Ctrl+V -- holding it open across the paste would deadlock
/// the very thing the strategy exists to do.
/// </para>
/// </remarks>
public sealed partial class Win32Clipboard : IClipboard
{
    private const uint GlobalMoveable = 0x0002;
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 10;

    public uint RegisterFormat(string name)
    {
        var id = RegisterClipboardFormat(name);
        return id == 0
            ? throw new InvalidOperationException($"RegisterClipboardFormat(\"{name}\") failed with error {Marshal.GetLastWin32Error()}.")
            : id;
    }

    public IReadOnlyList<uint> GetAvailableFormats()
    {
        using var session = Session.Open();

        List<uint> formats = [];
        uint format = 0;
        while ((format = EnumClipboardFormats(format)) != 0)
        {
            formats.Add(format);
        }

        return formats;
    }

    public byte[]? TryGetFormatData(uint format)
    {
        using var session = Session.Open();

        // A null handle here is the delayed-render promise. Jane treats it as "not available"
        // and moves on rather than triggering the render.
        var handle = GetClipboardData(format);
        if (handle == 0)
        {
            return null;
        }

        var size = (int)GlobalSize(handle);
        if (size <= 0)
        {
            return null;
        }

        var pointer = GlobalLock(handle);
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            var data = new byte[size];
            Marshal.Copy(pointer, data, 0, size);
            return data;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    public void SetContents(IReadOnlyList<ClipboardPayload> payloads)
    {
        using var session = Session.Open();

        if (!EmptyClipboard())
        {
            throw new InvalidOperationException($"EmptyClipboard failed with error {Marshal.GetLastWin32Error()}.");
        }

        foreach (var payload in payloads)
        {
            var handle = GlobalAlloc(GlobalMoveable, (nuint)payload.Data.Length);
            if (handle == 0)
            {
                throw new OutOfMemoryException($"GlobalAlloc failed for clipboard format {payload.Format}.");
            }

            var pointer = GlobalLock(handle);
            if (pointer == 0)
            {
                _ = GlobalFree(handle);
                throw new InvalidOperationException($"GlobalLock failed for clipboard format {payload.Format}.");
            }

            Marshal.Copy(payload.Data, 0, pointer, payload.Data.Length);
            _ = GlobalUnlock(handle);

            // Ownership of the handle transfers to the system on success only; on failure Jane
            // still owns it and must release it or the block leaks for the session.
            if (SetClipboardData(payload.Format, handle) == 0)
            {
                _ = GlobalFree(handle);
                throw new InvalidOperationException(
                    $"SetClipboardData failed for format {payload.Format} with error {Marshal.GetLastWin32Error()}.");
            }
        }
    }

    /// <summary>Holds the clipboard open for exactly as long as one operation needs it.</summary>
    private readonly struct Session : IDisposable
    {
        public static Session Open()
        {
            for (var attempt = 0; attempt < OpenAttempts; attempt++)
            {
                if (OpenClipboard(0))
                {
                    return default;
                }

                Thread.Sleep(OpenRetryDelayMs);
            }

            throw new InvalidOperationException(
                $"Another application held the clipboard for longer than {OpenAttempts * OpenRetryDelayMs} ms.");
        }

        public void Dispose() => CloseClipboard();
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint handle);
}
