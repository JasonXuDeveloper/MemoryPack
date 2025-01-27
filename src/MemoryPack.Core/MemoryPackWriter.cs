﻿using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MemoryPack;

public ref partial struct MemoryPackWriter<TBufferWriter>
    where TBufferWriter : IBufferWriter<byte>
{
    const int DepthLimit = 1000;

    ref TBufferWriter bufferWriter;
    ref byte bufferReference;
    int bufferLength;
    int advancedCount;
    int depth; // check recursive serialize
    int writtenCount;

    public int WrittenCount => writtenCount;

    public MemoryPackWriter(ref TBufferWriter writer)
    {
        this.bufferWriter = ref writer;
        this.bufferReference = ref Unsafe.NullRef<byte>();
        this.bufferLength = 0;
        this.advancedCount = 0;
        this.writtenCount = 0;
        this.depth = 0;
    }

    // optimized ctor, avoid first GetSpan call if we can.
    public MemoryPackWriter(ref TBufferWriter writer, byte[] firstBufferOfWriter)
    {
        this.bufferWriter = ref writer;
        this.bufferReference = ref MemoryMarshal.GetArrayDataReference(firstBufferOfWriter);
        this.bufferLength = firstBufferOfWriter.Length;
        this.advancedCount = 0;
        this.writtenCount = 0;
        this.depth = 0;
    }

    public MemoryPackWriter(ref TBufferWriter writer, Span<byte> firstBufferOfWriter)
    {
        this.bufferWriter = ref writer;
        this.bufferReference = ref MemoryMarshal.GetReference(firstBufferOfWriter);
        this.bufferLength = firstBufferOfWriter.Length;
        this.advancedCount = 0;
        this.writtenCount = 0;
        this.depth = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte GetSpanReference(int sizeHint)
    {
        if (bufferLength < sizeHint)
        {
            RequestNewBuffer(sizeHint);
        }

        return ref bufferReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void RequestNewBuffer(int sizeHint)
    {
        if (advancedCount != 0)
        {
            bufferWriter.Advance(advancedCount);
            advancedCount = 0;
        }
        var span = bufferWriter.GetSpan(sizeHint);
        bufferReference = ref MemoryMarshal.GetReference(span);
        bufferLength = span.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        var rest = bufferLength - count;
        if (rest < 0)
        {
            MemoryPackSerializationException.ThrowInvalidAdvance();
        }

        bufferLength = rest;
        bufferReference = ref Unsafe.Add(ref bufferReference, count);
        advancedCount += count;
        writtenCount += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Flush()
    {
        if (advancedCount != 0)
        {
            bufferWriter.Advance(advancedCount);
            advancedCount = 0;
        }
        bufferReference = ref Unsafe.NullRef<byte>();
        bufferLength = 0;
        writtenCount = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMemoryPackFormatter GetFormatter(Type type)
    {
        return MemoryPackFormatterProvider.GetFormatter(type);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMemoryPackFormatter<T> GetFormatter<T>()
    {
        return MemoryPackFormatterProvider.GetFormatter<T>();
    }

    // Write methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNullObjectHeader()
    {
        GetSpanReference(1) = MemoryPackCode.NullObject;
        Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteObjectHeader(byte memberCount)
    {
        if (memberCount >= MemoryPackCode.Reserved1)
        {
            MemoryPackSerializationException.ThrowWriteInvalidMemberCount(memberCount);
        }
        GetSpanReference(1) = memberCount;
        Advance(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnionHeader(byte tag)
    {
        ref var spanRef = ref GetSpanReference(2);
        spanRef = MemoryPackCode.Union;
        Unsafe.Add(ref spanRef, 1) = tag;
        Advance(2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCollectionHeader(int length)
    {
        Unsafe.WriteUnaligned(ref GetSpanReference(4), length);
        Advance(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteNullCollectionHeader()
    {
        Unsafe.WriteUnaligned(ref GetSpanReference(4), MemoryPackCode.NullCollection);
        Advance(4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteString(string? value)
    {
        if (value == null)
        {
            WriteNullCollectionHeader();
            return;
        }

        var copyByteCount = value.Length * 2;

        ref var dest = ref GetSpanReference(copyByteCount + 4);
        Unsafe.WriteUnaligned(ref dest, value.Length);

        if(copyByteCount > 0)
        {
            ref var src = ref Unsafe.As<char, byte>(ref Unsafe.AsRef(value.GetPinnableReference()));
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dest, 4), ref src, (uint)copyByteCount);
        }

        Advance(copyByteCount + 4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePackable<T>(scoped in T? value)
        where T : IMemoryPackable<T>
    {
        depth++;
        if (depth == DepthLimit) MemoryPackSerializationException.ThrowReachedDepthLimit(typeof(T));
        T.Serialize(ref this, ref Unsafe.AsRef(value));
        depth--;
    }

    // non packable, get formatter dynamically.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteObject<T>(scoped in T? value)
    {
        depth++;
        if (depth == DepthLimit) MemoryPackSerializationException.ThrowReachedDepthLimit(typeof(T));
        GetFormatter<T>().Serialize(ref this, ref Unsafe.AsRef(value));
        depth--;
    }

    #region WriteArray/Span

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteArray<T>(T?[]? value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            DangerousWriteUnmanagedArray(value);
            return;
        }

        if (value == null)
        {
            WriteNullCollectionHeader();
            return;
        }

        var formatter = GetFormatter<T>();
        WriteCollectionHeader(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            formatter.Serialize(ref this, ref value[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpan<T>(scoped Span<T?> value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            DangerousWriteUnmanagedSpan(value);
            return;
        }

        var formatter = GetFormatter<T>();
        WriteCollectionHeader(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            formatter.Serialize(ref this, ref value[i]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpan<T>(scoped ReadOnlySpan<T?> value)
    {
        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            DangerousWriteUnmanagedSpan(value);
            return;
        }

        var formatter = GetFormatter<T>();
        WriteCollectionHeader(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            formatter.Serialize(ref this, ref Unsafe.AsRef(value[i]));
        }
    }

    #endregion

    #region WriteUnmanagedArray/Span

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnmanagedArray<T>(T[]? value)
        where T : unmanaged
    {
        DangerousWriteUnmanagedArray(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnmanagedSpan<T>(scoped Span<T> value)
        where T : unmanaged
    {
        DangerousWriteUnmanagedSpan(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUnmanagedSpan<T>(scoped ReadOnlySpan<T> value)
        where T : unmanaged
    {
        DangerousWriteUnmanagedSpan(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DangerousWriteUnmanagedArray<T>(T[]? value)
    {
        if (value == null)
        {
            WriteNullCollectionHeader();
            return;
        }
        if (value.Length == 0)
        {
            WriteCollectionHeader(0);
            return;
        }

        var srcLength = Unsafe.SizeOf<T>() * value.Length;
        var allocSize = srcLength + 4;

        ref var dest = ref GetSpanReference(allocSize);
        ref var src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(value));

        Unsafe.WriteUnaligned(ref dest, value.Length);
        Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dest, 4), ref src, (uint)srcLength);

        Advance(allocSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DangerousWriteUnmanagedSpan<T>(scoped Span<T> value)
    {
        if (value.Length == 0)
        {
            WriteCollectionHeader(0);
            return;
        }

        var srcLength = Unsafe.SizeOf<T>() * value.Length;
        var allocSize = srcLength + 4;

        ref var dest = ref GetSpanReference(allocSize);
        ref var src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(value));

        Unsafe.WriteUnaligned(ref dest, value.Length);
        Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dest, 4), ref src, (uint)srcLength);

        Advance(allocSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DangerousWriteUnmanagedSpan<T>(scoped ReadOnlySpan<T> value)
    {
        if (value.Length == 0)
        {
            WriteCollectionHeader(0);
            return;
        }

        var srcLength = Unsafe.SizeOf<T>() * value.Length;
        var allocSize = srcLength + 4;

        ref var dest = ref GetSpanReference(allocSize);
        ref var src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(value));

        Unsafe.WriteUnaligned(ref dest, value.Length);
        Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dest, 4), ref src, (uint)srcLength);

        Advance(allocSize);
    }

    #endregion


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpanWithoutLengthHeader<T>(scoped ReadOnlySpan<T?> value)
    {
        if (value.Length == 0) return;

        if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            var srcLength = Unsafe.SizeOf<T>() * value.Length;
            ref var dest = ref GetSpanReference(srcLength);
            ref var src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(value)!);

            Unsafe.CopyBlockUnaligned(ref dest, ref src, (uint)srcLength);

            Advance(srcLength);
            return;
        }
        else
        {
            var formatter = GetFormatter<T>();
            for (int i = 0; i < value.Length; i++)
            {
                formatter.Serialize(ref this, ref Unsafe.AsRef(value[i]));
            }
        }
    }
}
