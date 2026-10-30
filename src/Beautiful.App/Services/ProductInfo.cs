using System.Reflection;

namespace Beautiful.App.Services;

/// <summary>产品版本单一出口（设置窗口/关于窗口同源，避免两处各写一份反射代码）。</summary>
public static class ProductInfo
{
    public static string Version
    {
        get
        {
            var v = typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(v) ? "未知" : v.Split('+')[0];
        }
    }
}
