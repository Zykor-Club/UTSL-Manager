/// <summary>
/// 原生调用：EmptyWorkingSet（psapi.dll）、GetOEMCP（kernel32）。
/// ★ 单独一个文件是刻意的：Program.cs 是顶层语句文件，类型声明只能放在所有顶层语句【之后】，
///   放到别的文件里最省事、也不会再踩"必须位于命名空间和类型声明之前"的编译错误。
/// </summary>
internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 系统 OEM 代码页（中文 Windows = 936）。★ 必须走 API：agent 的 csproj 是
    /// InvariantGlobalization=true，CurrentCulture.TextInfo.OEMCodePage 拿不到真值（会退化成 1252），
    /// 结果 .cmd 里的中文路径被写成"?"。
    /// </summary>
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern int GetOEMCP();
}
