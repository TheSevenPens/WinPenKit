using System.Runtime.InteropServices;

namespace WinPenKit.Wintab;

/// <summary>
/// RAII wrapper for unmanaged memory allocated via <see cref="Marshal.AllocHGlobal"/>.
/// Guarantees cleanup via <see cref="IDisposable"/>.
/// </summary>
internal sealed class UnmanagedBuffer : IDisposable
{
    public IntPtr Ptr { get; private set; }
    public int Size { get; }

    private UnmanagedBuffer(int size)
    {
        Size = size;
        Ptr = Marshal.AllocHGlobal(size);
        // Zero-initialize to avoid uninitialized data in structs.
        unsafe
        {
            new Span<byte>((void*)Ptr, size).Clear();
        }
    }

    /// <summary>Creates a buffer large enough for one instance of <typeparamref name="T"/>.</summary>
    public static UnmanagedBuffer Create<T>() where T : struct =>
        new(Marshal.SizeOf<T>());

    /// <summary>Marshals the buffer content to a managed struct.</summary>
    public T MarshalOut<T>() where T : struct =>
        Marshal.PtrToStructure<T>(Ptr);

    /// <summary>Marshals a managed struct into the buffer.</summary>
    public void MarshalIn<T>(T value) where T : struct =>
        Marshal.StructureToPtr(value, Ptr, false);

    public void Dispose()
    {
        if (Ptr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Ptr);
            Ptr = IntPtr.Zero;
        }
    }
}
