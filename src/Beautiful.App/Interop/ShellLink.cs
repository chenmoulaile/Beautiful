using System.Runtime.InteropServices;

namespace Beautiful.App.Interop;

// .lnk 解析：WScript.Shell COM（系统自带，免额外互操作程序集）。
internal static class ShellLink
{
    public sealed record Resolved(string TargetPath, string Arguments, string IconLocation, string WorkingDirectory);

    public static Resolved Resolve(string lnkPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return new Resolved(lnkPath, "", lnkPath, "");
        dynamic? shell = null;
        dynamic? link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            link = shell!.CreateShortcut(lnkPath);
            string target = (string)link.TargetPath;
            if (string.IsNullOrEmpty(target)) target = lnkPath;
            return new Resolved(target, (string)link.Arguments, (string)link.IconLocation, (string)link.WorkingDirectory);
        }
        catch
        {
            return new Resolved(lnkPath, "", lnkPath, "");
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
