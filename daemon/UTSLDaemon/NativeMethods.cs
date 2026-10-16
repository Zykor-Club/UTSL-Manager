/// <summary>
/// 原生调用：EmptyWorkingSet（psapi.dll）。
/// ★ 单独一个文件是刻意的：Program.cs 是顶层语句文件，类型声明只能放在所有顶层语句【之后】，
///   放到别的文件里最省事、也不会再踩"必须位于命名空间和类型声明之前"的编译错误。
/// </summary>
internal static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    public static extern bool EmptyWorkingSet(IntPtr hProcess);
}
