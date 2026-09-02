using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jane.Windows.Automation;

/// <param name="ConnectionTimeoutMs">
/// How long UI Automation waits to establish a connection to a provider before giving up. The
/// system default is 2000 ms, which is twenty-five times Jane's whole context budget. Setting it
/// low is the only lever a client has over a provider that is not answering, and it is exactly why
/// this file exists: the managed <c>UIAutomationClient</c> wrapper does not expose it.
/// </param>
/// <param name="TransactionTimeoutMs">
/// How long a single cross-process call may take. Also 2000 ms by default. Lower than the default
/// but well above the read budget, because a legitimately large document range can be slow and
/// cutting it off would lose a usable answer, whereas the worker's own deadline already protects
/// the pipeline.
/// </param>
/// <param name="AutoSetFocus">
/// UI Automation will move focus to satisfy some calls unless this is turned off. Jane reads while
/// the user is speaking into somebody else's window; moving their focus would be catastrophic.
/// </param>
public sealed record UiaComOptions(
    uint ConnectionTimeoutMs = 100,
    uint TransactionTimeoutMs = 1000,
    bool AutoSetFocus = false)
{
    public static UiaComOptions Default { get; } = new();
}

/// <summary>Control types Jane makes decisions on. Values from the UIAutomationCore type library.</summary>
public static class UiaControlType
{
    public const int ComboBox = 50003;
    public const int Edit = 50004;
    public const int Hyperlink = 50005;
    public const int List = 50008;
    public const int Text = 50020;
    public const int Custom = 50025;
    public const int Group = 50026;
    public const int Document = 50030;
    public const int Window = 50032;
    public const int Pane = 50033;
}

/// <summary>
/// A live UI Automation client, talked to over raw COM.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw COM.</b> There is no in-box UI Automation <em>client</em> library for a
/// <c>net10.0-windows</c> project that does not switch on WPF or WinForms, and Jane.Windows
/// switches on neither. More importantly, the managed wrapper has never exposed
/// <c>IUIAutomation2::put_ConnectionTimeout</c>, and a low connection timeout is a hard
/// requirement of this phase -- without it the only bound on a wedged provider is the worker's
/// own deadline, and the thread stays stuck for two full seconds per attempt.
/// </para>
/// <para>
/// <b>How the vtable offsets were obtained.</b> Not from memory and not from MSDN, which lists
/// methods alphabetically rather than in vtable order. They were read out of the type library
/// inside <c>UIAutomationCore.dll</c> on this machine (<c>ITypeInfo.GetFuncDesc().oVft</c>,
/// divided by the 8-byte pointer size), so every constant below is the layout this OS actually
/// ships. <c>ConnectionTimeoutRoundTrips</c> re-checks the riskiest of them at runtime.
/// </para>
/// <para>
/// <b>Threading.</b> Every method must be called on the thread that constructed the instance, and
/// that thread must be MTA with no message pump -- which is what <see cref="UiaWorker"/> provides.
/// No COM pointer ever leaves this class: <see cref="Read"/> returns managed strings and numbers
/// only, so a wedged provider can never leave a live interface pointer stranded on another thread.
/// </para>
/// <para>
/// Every failure degrades to a status. Deep Context is an accuracy improvement, not a dependency;
/// a machine where UI Automation refuses to start still dictates perfectly well.
/// </para>
/// </remarks>
public sealed unsafe partial class UiaComSession : IUiaSession
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    private nint _automation;
    private nint _cacheRequest;
    private bool _disposed;

    public UiaComSession(UiaComOptions? options = null)
    {
        Options = options ?? UiaComOptions.Default;

        _automation = CreateAutomation(out var supportsTimeouts);
        if (_automation == 0)
        {
            return;
        }

        SupportsTimeouts = supportsTimeouts;
        if (supportsTimeouts)
        {
            // A failure here is not fatal: it just means the read is bounded by the worker's
            // deadline alone rather than by UI Automation as well.
            TimeoutsApplied =
                Hr(Uia.PutConnectionTimeout(_automation, Options.ConnectionTimeoutMs)) &&
                Hr(Uia.PutTransactionTimeout(_automation, Options.TransactionTimeoutMs));

            _ = Uia.PutAutoSetFocus(_automation, Options.AutoSetFocus ? 1 : 0);
        }

        _cacheRequest = CreateCacheRequest(_automation);
    }

    public UiaComOptions Options { get; }

    /// <summary>False when UI Automation could not be created at all.</summary>
    public bool IsAvailable => _automation != 0 && _cacheRequest != 0;

    /// <summary>Whether the OS gave us <c>IUIAutomation2</c>, which is where the timeouts live.</summary>
    public bool SupportsTimeouts { get; }

    /// <summary>Whether the configured timeouts were actually accepted by the provider host.</summary>
    public bool TimeoutsApplied { get; }

    /// <summary>
    /// Reads back the connection timeout that was set.
    /// </summary>
    /// <remarks>
    /// A diagnostic, and the one runtime check on the hand-derived vtable layout: if the offsets
    /// for the <c>IUIAutomation2</c> property pair were wrong, this would not round-trip.
    /// </remarks>
    public uint? ConnectionTimeoutRoundTrips()
    {
        if (!SupportsTimeouts || _automation == 0)
        {
            return null;
        }

        uint value;
        return Hr(Uia.GetConnectionTimeout(_automation, &value)) ? value : null;
    }

    /// <summary>
    /// One Deep Context read: the focused element and its cached properties in a single
    /// cross-process transaction, then the text calls that cannot be cached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cache request is what makes the first stage one round trip instead of nine. Selection
    /// and text are method calls on a text range, not properties, so they cannot be cached and
    /// each costs a further trip -- which is why the stages are ordered by value and the elapsed
    /// time is checked between them. A read that has already spent its budget stops and returns
    /// what it has; a partial answer beats no answer, and it beats an answer that arrives after
    /// the pipeline has moved on.
    /// </para>
    /// </remarks>
    public UiaRawRead Read(UiaReadRequest request)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            // Never a user-visible failure -- a bug in Jane. The rule exists because a UIA client
            // pointer used from a second apartment marshals, and marshalling needs a pump.
            return UiaRawRead.Failed(UiaReadStatus.ProviderError, "UIA session used off its owning thread.");
        }

        if (!IsAvailable)
        {
            return UiaRawRead.Failed(UiaReadStatus.Unavailable, "UI Automation is not available in this session.");
        }

        var clock = Stopwatch.StartNew();
        var roundTrips = 0;

        nint element;
        if (!Hr(Uia.GetFocusedElementBuildCache(_automation, _cacheRequest, &element)) || element == 0)
        {
            return UiaRawRead.Failed(UiaReadStatus.NoFocusedElement, "No focused element.") with
            {
                Elapsed = clock.Elapsed,
                RoundTrips = ++roundTrips,
            };
        }

        roundTrips++;

        try
        {
            // Everything below this line up to the text pattern is served from the cache the one
            // round trip above already filled. It costs nothing.
            int passwordFlag;
            var isPassword = Hr(Uia.ElementCachedIsPassword(element, &passwordFlag)) && passwordFlag != 0;

            int processId;
            if (!Hr(Uia.ElementCachedProcessId(element, &processId)))
            {
                processId = 0;
            }

            if (isPassword)
            {
                // The hard rule. Not "read it and drop it" -- not read at all.
                return new UiaRawRead
                {
                    Status = UiaReadStatus.PasswordControl,
                    IsPassword = true,
                    ProcessId = processId,
                    Elapsed = clock.Elapsed,
                    RoundTrips = roundTrips,
                };
            }

            if (request.Target.ProcessId != 0 && processId != 0 && processId != request.Target.ProcessId)
            {
                // Focus moved between key-down and the read. Reading on would describe a window
                // Jane is not dictating into, which is the wrong context and a privacy leak.
                return new UiaRawRead
                {
                    Status = UiaReadStatus.FocusMoved,
                    ProcessId = processId,
                    Elapsed = clock.Elapsed,
                    RoundTrips = roundTrips,
                    Detail = $"Focused element belongs to process {processId}, not {request.Target.ProcessId}.",
                };
            }

            int controlType;
            if (!Hr(Uia.ElementCachedControlType(element, &controlType)))
            {
                controlType = 0;
            }

            nint name;
            nint className;
            nint frameworkId;

            var read = new UiaRawRead
            {
                Status = UiaReadStatus.Ok,
                ProcessId = processId,
                ControlTypeId = controlType,
                ControlName = Hr(Uia.ElementCachedName(element, &name)) ? TrimToNull(TakeBstr(name)) : null,
                ClassName = Hr(Uia.ElementCachedClassName(element, &className)) ? TrimToNull(TakeBstr(className)) : null,
                FrameworkId = Hr(Uia.ElementCachedFrameworkId(element, &frameworkId)) ? TrimToNull(TakeBstr(frameworkId)) : null,
                Value = CachedPatternValue(element),
                Caret = CachedBounds(element),
            };

            var textPattern = GetCachedPattern(element, Uia.TextPatternId);
            if (textPattern == 0)
            {
                return read with
                {
                    Status = read.Value is null ? UiaReadStatus.NoTextProvider : UiaReadStatus.Ok,
                    Elapsed = clock.Elapsed,
                    RoundTrips = roundTrips,
                };
            }

            try
            {
                return ReadText(textPattern, request, read, clock, ref roundTrips);
            }
            finally
            {
                Release(textPattern);
            }
        }
        catch (Exception ex)
        {
            // Nothing a provider does is allowed to become a crash in Jane: Deep Context is an
            // accuracy improvement, and losing it costs one dictation a few hotwords.
            return UiaRawRead.Failed(UiaReadStatus.ProviderError, ex.Message) with
            {
                Elapsed = clock.Elapsed,
                RoundTrips = roundTrips,
            };
        }
        finally
        {
            Release(element);
        }
    }

    private UiaRawRead ReadText(
        nint textPattern,
        UiaReadRequest request,
        UiaRawRead read,
        Stopwatch clock,
        ref int roundTrips)
    {
        var range = FirstSelectionRange(textPattern, ref roundTrips);
        var fromCaret = false;

        if (range == 0)
        {
            // Chromium reports no selection ranges at all in some documents; the caret range is
            // the same anchor by another name, and it is the only one those providers answer.
            range = CaretRange(textPattern, ref roundTrips);
            fromCaret = range != 0;
        }

        if (range == 0)
        {
            return read with { Elapsed = clock.Elapsed, RoundTrips = roundTrips };
        }

        try
        {
            if (!fromCaret && Budget(clock, request))
            {
                read = read with { Selection = TrimToNull(RangeText(range, request.MaxSelectionChars, ref roundTrips)) };
            }

            if (Budget(clock, request) && RangeBounds(range, ref roundTrips) is { IsEmpty: false } caret)
            {
                // Preferred over the element's rectangle: it is the caret, not the whole control.
                read = read with { Caret = caret };
            }

            if (Budget(clock, request))
            {
                // Expanding the range Jane already holds, rather than cloning it first, saves a
                // round trip; the selection text has already been taken off it by this point.
                if (Hr(Uia.RangeExpandToEnclosingUnit(range, Uia.TextUnitParagraph)))
                {
                    roundTrips++;
                    read = read with { Surrounding = TrimToNull(RangeText(range, request.MaxSurroundingChars, ref roundTrips)) };
                }
            }
            else
            {
                read = read with { BudgetExpired = true };
            }

            return read with { Elapsed = clock.Elapsed, RoundTrips = roundTrips };
        }
        finally
        {
            Release(range);
        }
    }

    private static bool Budget(Stopwatch clock, UiaReadRequest request) => clock.Elapsed < request.SoftBudget;

    private static nint FirstSelectionRange(nint textPattern, ref int roundTrips)
    {
        nint array;
        if (!Hr(Uia.TextGetSelection(textPattern, &array)) || array == 0)
        {
            roundTrips++;
            return 0;
        }

        roundTrips++;

        try
        {
            int length;
            if (!Hr(Uia.RangeArrayLength(array, &length)) || length <= 0)
            {
                return 0;
            }

            nint range;
            return Hr(Uia.RangeArrayGetElement(array, 0, &range)) ? range : 0;
        }
        finally
        {
            Release(array);
        }
    }

    private static nint CaretRange(nint textPattern, ref int roundTrips)
    {
        var textPattern2 = QueryInterface(textPattern, Uia.IidTextPattern2);
        if (textPattern2 == 0)
        {
            return 0;
        }

        try
        {
            int active;
            nint range;
            roundTrips++;
            return Hr(Uia.TextGetCaretRange(textPattern2, &active, &range)) ? range : 0;
        }
        finally
        {
            Release(textPattern2);
        }
    }

    private static string? RangeText(nint range, int maxChars, ref int roundTrips)
    {
        nint bstr;
        roundTrips++;
        return Hr(Uia.RangeGetText(range, maxChars, &bstr)) ? TakeBstr(bstr) : null;
    }

    private static CaretRect? RangeBounds(nint range, ref int roundTrips)
    {
        nint safeArray;
        roundTrips++;
        if (!Hr(Uia.RangeGetBoundingRectangles(range, &safeArray)) || safeArray == 0)
        {
            return null;
        }

        try
        {
            if (SafeArrayGetLBound(safeArray, 1, out var lower) != 0 ||
                SafeArrayGetUBound(safeArray, 1, out var upper) != 0 ||
                upper - lower + 1 < 4)
            {
                return null;
            }

            if (SafeArrayAccessData(safeArray, out var data) != 0)
            {
                return null;
            }

            try
            {
                // UI Automation reports text-range rectangles as left, top, width, height --
                // deliberately not the RECT edges the element property uses.
                var values = (double*)data;
                return new CaretRect(values[0], values[1], values[2], values[3]);
            }
            finally
            {
                _ = SafeArrayUnaccessData(safeArray);
            }
        }
        finally
        {
            _ = SafeArrayDestroy(safeArray);
        }
    }

    private static string? CachedPatternValue(nint element)
    {
        var pattern = GetCachedPattern(element, Uia.ValuePatternId);
        if (pattern == 0)
        {
            return null;
        }

        try
        {
            nint bstr;
            return Hr(Uia.ValueCachedValue(pattern, &bstr)) ? TrimToNull(TakeBstr(bstr)) : null;
        }
        finally
        {
            Release(pattern);
        }
    }

    private static CaretRect? CachedBounds(nint element)
    {
        UiaRect rect;
        if (!Hr(Uia.ElementCachedBoundingRectangle(element, &rect)))
        {
            return null;
        }

        return rect.Right <= rect.Left && rect.Bottom <= rect.Top
            ? null
            : CaretRect.FromEdges(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static nint GetCachedPattern(nint element, int patternId)
    {
        nint pattern;
        return Hr(Uia.ElementGetCachedPattern(element, patternId, &pattern)) ? pattern : 0;
    }

    private static nint CreateAutomation(out bool supportsTimeouts)
    {
        supportsTimeouts = false;

        // CUIAutomation8 is what implements IUIAutomation2 and up. The plain CUIAutomation class
        // is kept as a fallback so a machine that only has the original interface still gets Deep
        // Context, just without the timeouts.
        var clsid8 = Uia.ClsidCUIAutomation8;
        var iid2 = Uia.IidUIAutomation2;
        nint instance;
        if (CoCreateInstance(&clsid8, 0, ClsCtxInprocServer, &iid2, &instance) == 0 && instance != 0)
        {
            supportsTimeouts = true;
            return instance;
        }

        var clsid = Uia.ClsidCUIAutomation;
        var iid = Uia.IidUIAutomation;
        return CoCreateInstance(&clsid, 0, ClsCtxInprocServer, &iid, &instance) == 0 ? instance : 0;
    }

    /// <summary>
    /// The one cache request, built once and reused for every read.
    /// </summary>
    /// <remarks>
    /// Every property named here comes back in the same transaction as the element itself. The
    /// list is exactly what the policy layer needs and nothing else -- <c>IsPassword</c> because
    /// it gates the whole read, <c>ProcessId</c> because a focus change between key-down and the
    /// read must be detectable, and the rest because they cost nothing once the trip is being
    /// made anyway.
    /// <para>
    /// The element mode is <c>Full</c>, not <c>None</c>. <c>None</c> is cheaper but hands back an
    /// element with no connection to the provider, and the text pattern needs a live one.
    /// </para>
    /// </remarks>
    private static nint CreateCacheRequest(nint automation)
    {
        nint request;
        if (!Hr(Uia.CreateCacheRequest(automation, &request)) || request == 0)
        {
            return 0;
        }

        foreach (var property in Uia.CachedProperties)
        {
            _ = Uia.CacheAddProperty(request, property);
        }

        _ = Uia.CacheAddPattern(request, Uia.ValuePatternId);
        _ = Uia.CacheAddPattern(request, Uia.TextPatternId);
        _ = Uia.CachePutTreeScope(request, Uia.TreeScopeElement);
        _ = Uia.CachePutAutomationElementMode(request, Uia.AutomationElementModeFull);

        return request;
    }

    private static nint QueryInterface(nint instance, Guid iid)
    {
        nint result;
        var vtable = *(nint**)instance;
        var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)vtable[0])(instance, &iid, &result);
        return hr == 0 ? result : 0;
    }

    private static string? TakeBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool Hr(int hr) => hr >= 0;

    private static void Release(nint instance)
    {
        if (instance != 0)
        {
            _ = Marshal.Release(instance);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release(_cacheRequest);
        Release(_automation);
        _cacheRequest = 0;
        _automation = 0;
    }

    private const uint ClsCtxInprocServer = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct UiaRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayGetLBound(nint array, uint dimension, out int bound);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayGetUBound(nint array, uint dimension, out int bound);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayAccessData(nint array, out nint data);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayUnaccessData(nint array);

    [LibraryImport("oleaut32.dll")]
    private static partial int SafeArrayDestroy(nint array);

    /// <summary>
    /// The raw vtable surface. Every slot index below is <c>oVft / 8</c> read from the type
    /// library in <c>UIAutomationCore.dll</c>, so the numbers are this operating system's own
    /// answer rather than a transcription of a header.
    /// </summary>
    private static class Uia
    {
        internal static Guid ClsidCUIAutomation { get; } = new("ff48dba4-60ef-4201-aa87-54103eef594e");

        internal static Guid ClsidCUIAutomation8 { get; } = new("e22ad333-b25f-460c-83d0-0581107395c9");

        internal static Guid IidUIAutomation { get; } = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

        internal static Guid IidUIAutomation2 { get; } = new("34723aff-0c9d-49d0-9896-7ab52df8cd8a");

        internal static Guid IidTextPattern2 { get; } = new("506a921a-fcc9-409f-b23b-37eb74106872");

        internal const int ValuePatternId = 10002;
        internal const int TextPatternId = 10014;

        internal const int TreeScopeElement = 1;
        internal const int AutomationElementModeFull = 1;
        internal const int TextUnitParagraph = 4;

        /// <summary>Everything fetched in the single cache-filling round trip.</summary>
        internal static ReadOnlySpan<int> CachedProperties =>
        [
            30001, // BoundingRectangle
            30002, // ProcessId
            30003, // ControlType
            30005, // Name
            30008, // HasKeyboardFocus
            30012, // ClassName
            30019, // IsPassword
            30020, // NativeWindowHandle
            30024, // FrameworkId
            30040, // IsTextPatternAvailable
            30043, // IsValuePatternAvailable
            30045, // Value.Value
            30046, // Value.IsReadOnly
        ];

        // ---- IUIAutomation (slots 3..57) and IUIAutomation2 (58..63) -------------------------
        internal static int GetFocusedElementBuildCache(nint self, nint cacheRequest, nint* element) =>
            ((delegate* unmanaged<nint, nint, nint*, int>)Slot(self, 12))(self, cacheRequest, element);

        internal static int CreateCacheRequest(nint self, nint* request) =>
            ((delegate* unmanaged<nint, nint*, int>)Slot(self, 20))(self, request);

        internal static int PutAutoSetFocus(nint self, int value) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 59))(self, value);

        internal static int GetConnectionTimeout(nint self, uint* value) =>
            ((delegate* unmanaged<nint, uint*, int>)Slot(self, 60))(self, value);

        internal static int PutConnectionTimeout(nint self, uint value) =>
            ((delegate* unmanaged<nint, uint, int>)Slot(self, 61))(self, value);

        internal static int PutTransactionTimeout(nint self, uint value) =>
            ((delegate* unmanaged<nint, uint, int>)Slot(self, 63))(self, value);

        // ---- IUIAutomationCacheRequest -------------------------------------------------------
        internal static int CacheAddProperty(nint self, int propertyId) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 3))(self, propertyId);

        internal static int CacheAddPattern(nint self, int patternId) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 4))(self, patternId);

        internal static int CachePutTreeScope(nint self, int scope) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 7))(self, scope);

        internal static int CachePutAutomationElementMode(nint self, int mode) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 11))(self, mode);

        // ---- IUIAutomationElement ------------------------------------------------------------
        internal static int ElementGetCachedPattern(nint self, int patternId, nint* pattern) =>
            ((delegate* unmanaged<nint, int, nint*, int>)Slot(self, 17))(self, patternId, pattern);

        internal static int ElementCachedProcessId(nint self, int* value) => GetInt(self, 52, value);

        internal static int ElementCachedControlType(nint self, int* value) => GetInt(self, 53, value);

        internal static int ElementCachedName(nint self, nint* value) => GetPtr(self, 55, value);

        internal static int ElementCachedClassName(nint self, nint* value) => GetPtr(self, 62, value);

        internal static int ElementCachedIsPassword(nint self, int* value) => GetInt(self, 67, value);

        internal static int ElementCachedFrameworkId(nint self, nint* value) => GetPtr(self, 72, value);

        internal static int ElementCachedBoundingRectangle(nint self, UiaRect* value) =>
            ((delegate* unmanaged<nint, UiaRect*, int>)Slot(self, 75))(self, value);

        // ---- IUIAutomationValuePattern -------------------------------------------------------
        internal static int ValueCachedValue(nint self, nint* value) => GetPtr(self, 6, value);

        // ---- IUIAutomationTextPattern / TextPattern2 / TextRangeArray / TextRange -------------
        internal static int TextGetSelection(nint self, nint* rangeArray) => GetPtr(self, 5, rangeArray);

        internal static int TextGetCaretRange(nint self, int* isActive, nint* range) =>
            ((delegate* unmanaged<nint, int*, nint*, int>)Slot(self, 10))(self, isActive, range);

        internal static int RangeArrayLength(nint self, int* length) => GetInt(self, 3, length);

        internal static int RangeArrayGetElement(nint self, int index, nint* range) =>
            ((delegate* unmanaged<nint, int, nint*, int>)Slot(self, 4))(self, index, range);

        internal static int RangeExpandToEnclosingUnit(nint self, int unit) =>
            ((delegate* unmanaged<nint, int, int>)Slot(self, 6))(self, unit);

        internal static int RangeGetBoundingRectangles(nint self, nint* safeArray) => GetPtr(self, 10, safeArray);

        internal static int RangeGetText(nint self, int maxLength, nint* text) =>
            ((delegate* unmanaged<nint, int, nint*, int>)Slot(self, 12))(self, maxLength, text);

        private static nint Slot(nint self, int index) => (*(nint**)self)[index];

        private static int GetInt(nint self, int slot, int* value) =>
            ((delegate* unmanaged<nint, int*, int>)Slot(self, slot))(self, value);

        private static int GetPtr(nint self, int slot, nint* value) =>
            ((delegate* unmanaged<nint, nint*, int>)Slot(self, slot))(self, value);
    }
}
