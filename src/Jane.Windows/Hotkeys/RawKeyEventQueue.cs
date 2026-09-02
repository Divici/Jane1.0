using System.Numerics;

namespace Jane.Windows.Hotkeys;

/// <param name="VirtualKey">The VK_ code exactly as the hook reported it -- VK_RCONTROL, not VK_CONTROL.</param>
/// <param name="IsDown">Key-down (including auto-repeat) rather than key-up.</param>
/// <param name="Timestamp">
/// <see cref="System.Diagnostics.Stopwatch"/> ticks. Stopwatch is used rather than a wall clock
/// because reading it is a single instruction with no allocation, which is all the hook proc is
/// allowed to do, and because hold durations must not jump when the system clock is adjusted.
/// </param>
public readonly record struct RawKeyEvent(int VirtualKey, bool IsDown, long Timestamp);

/// <summary>
/// A fixed-size, lock-free ring between the keyboard hook and the pump that interprets it.
/// </summary>
/// <remarks>
/// Single producer (the hook procedure), single consumer (the pump thread), so the head and tail
/// indices need only ordered writes, never interlocked ones. Nothing here allocates after
/// construction and nothing blocks: a hook callback that waited on a lock, grew a list or
/// triggered a GC could overrun <c>LowLevelHooksTimeout</c>, at which point Windows removes the
/// hook and the hotkey stops working with no error anywhere.
/// <para>
/// When the ring is full the newest event is dropped and counted rather than overwriting an
/// older one -- losing the key-up of a hold that has already started would strand a dictation,
/// whereas losing a key-down merely misses a press the user can repeat.
/// </para>
/// </remarks>
public sealed class RawKeyEventQueue
{
    private readonly RawKeyEvent[] _slots;
    private readonly int _mask;

    private int _head;
    private int _tail;
    private int _dropped;

    /// <param name="capacity">
    /// Usable slots, rounded up so the ring length is a power of two and the wrap is a mask
    /// rather than a division.
    /// </param>
    public RawKeyEventQueue(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

        // One slot is always left empty so "full" and "empty" are distinguishable without a
        // separate count that both threads would have to agree on.
        var length = (int)BitOperations.RoundUpToPowerOf2((uint)capacity + 1);
        _slots = new RawKeyEvent[length];
        _mask = length - 1;
    }

    /// <summary>How many events fit before <see cref="TryEnqueue"/> starts dropping.</summary>
    public int Capacity => _slots.Length - 1;

    /// <summary>Events lost to a full ring. Non-zero means the pump stalled; it is worth logging.</summary>
    public int DroppedCount => Volatile.Read(ref _dropped);

    /// <summary>Producer side. Allocation-free, lock-free, wait-free.</summary>
    public bool TryEnqueue(in RawKeyEvent keyEvent)
    {
        var head = _head;
        var next = (head + 1) & _mask;
        if (next == Volatile.Read(ref _tail))
        {
            _dropped++;
            return false;
        }

        _slots[head] = keyEvent;
        Volatile.Write(ref _head, next);
        return true;
    }

    /// <summary>Consumer side.</summary>
    public bool TryDequeue(out RawKeyEvent keyEvent)
    {
        var tail = _tail;
        if (tail == Volatile.Read(ref _head))
        {
            keyEvent = default;
            return false;
        }

        keyEvent = _slots[tail];
        Volatile.Write(ref _tail, (tail + 1) & _mask);
        return true;
    }
}
