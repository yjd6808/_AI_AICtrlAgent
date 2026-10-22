using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AiCtrl.Client.Workspace;

public static class RecycleBin
{
    private const uint FOF_SILENT = 0x0004;

    private const uint FOF_NOCONFIRMATION = 0x0010;

    private const uint FOF_ALLOWUNDO = 0x0040;

    private const uint FOF_NOERRORUI = 0x0400;

    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var operation = new ShFileOperation
        {
            Hwnd = 0,
            Func = 0x0003,
            From = path + '\0' + '\0',
            Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };

        var result = SHFileOperationW(ref operation);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        if (operation.AnyOperationsAborted)
        {
            throw new IOException($"Deleting '{path}' was aborted.");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOperation operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOperation
    {
        public nint Hwnd;

        public uint Func;

        public string From;

        public string? To;

        public uint Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;

        public nint NameMappings;

        public string? ProgressTitle;
    }
}
