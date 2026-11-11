global using System.IO;

// Native DLLs (user32, dwmapi, gdi32, shell32) are only ever loaded from System32,
// never from the app folder or current directory (DLL planting).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
