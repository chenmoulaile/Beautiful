# Beautiful lite 包（framework-dependent 极简形态）使用说明

这个包与标准包（Beautiful-win-x64-*.zip / Beautiful-Setup-*.exe）的差别：

  lite 包不含 .NET 运行时，体积小一个量级，**必须先安装 .NET Desktop Runtime 10** 才能运行：
  https://dotnet.microsoft.com/download/dotnet/10.0

【解压位置务必注意】

  请解压到一个**全新的空目录**再运行，不要把包内容覆盖进旧版本的安装目录。

  原因：5.0 及更早的标准包是 self-contained（自带 .NET 8 运行时），
  旧目录里残留的 hostfxr.dll / hostpolicy.dll / coreclr.dll / System.Private.CoreLib.dll
  是 .NET 8 的。lite 包（framework-dependent）不带这些文件，覆盖解压后旧 8.0 宿主仍留在
  目录里，应用启动时 app-local 的 8.0 hostfxr 会抢先加载并把运行时查找锁定在应用目录，
  读到 net10 的 runtimeconfig 即报：

      "You must install or update .NET to run this application."
      （框架解析失败：No frameworks were found）

  应急处置：把安装目录里的 hostfxr.dll、hostpolicy.dll、coreclr.dll、System.Private.CoreLib.dll
  及其它 System.*.dll / Microsoft.*.dll 旧运行时文件清掉（或整个目录换新）即可恢复；
  走安装器（Beautiful-Setup）覆盖安装则会自动清理，无需手工处理。

【配置与日志】写在 exe 旁边的 config\ 、logs\ 、cache\ 目录，换目录运行等于全新配置。
