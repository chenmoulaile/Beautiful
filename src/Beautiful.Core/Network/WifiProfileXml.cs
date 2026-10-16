using System.Text;

namespace Beautiful.Core.Network;

/// <summary>
/// 生成 <c>WlanSetProfile</c> 要的 WLANProfile XML（schema：
/// <c>http://www.microsoft.com/networking/WLAN/profile/v1</c>）。
/// <para>
/// 放在 Core 是为了把两件容易出事的事关在可单测的地方：<b>SSID 必须 XML 转义</b>（网络名是外部输入，
/// 含 <c>&amp; &lt; &gt; "</c> 时不转义就是结构损坏甚至注入到别的字段），以及<b>口令绝不进日志</b>——
/// 本方法返回的串含明文 <c>keyMaterial</c>，调用方只能把它交给 wlanapi，不得写日志、不得落盘。
/// </para>
/// <para>
/// <c>authentication</c> 的取值域里没有 "WPA2PSK" 这种写法：个人版就是 <c>WPA2</c> + <c>AES</c>
/// 且 <c>useOneX=false</c>；<c>WPA2</c> 配合 <c>useOneX=true</c> 才是企业级。
/// </para>
/// </summary>
public static class WifiProfileXml
{
    /// <summary>SSID/口令里不允许的控制字符（会让 XML 与 wlanapi 双双行为异常）。</summary>
    private static bool HasControlChars(string s) => s.Any(c => char.IsControl(c));

    /// <summary>
    /// 构造 profile XML。
    /// </summary>
    /// <param name="ssid">网络名，非空且不含控制字符。</param>
    /// <param name="authAlgorithm"><c>DOT11_AUTH_ALGORITHM</c> 值（先过 <see cref="WifiSecurity.CanJoinDirectly"/>）。</param>
    /// <param name="passphrase">口令；开放网络传 null 或空。</param>
    /// <param name="hidden">是否隐藏网络（决定 <c><nonBroadcast>true</nonBroadcast></c>）。</param>
    /// <exception cref="ArgumentException">SSID 非法、或需要口令的加密方式没给口令。</exception>
    public static string Build(string ssid, int authAlgorithm, string? passphrase, bool hidden = false)
    {
        if (string.IsNullOrEmpty(ssid)) throw new ArgumentException("SSID 不能为空", nameof(ssid));
        if (HasControlChars(ssid)) throw new ArgumentException("SSID 含控制字符", nameof(ssid));

        var (authToken, encToken) = authAlgorithm switch
        {
            WifiSecurity.Auth80211Open => ("open", "none"),
            WifiSecurity.Auth80211Shared => ("shared", "WEP"),
            WifiSecurity.AuthWpa => ("WPA", "TKIP"),
            WifiSecurity.AuthWpa2 => ("WPA2", "AES"),
            WifiSecurity.AuthWpa3 => ("WPA3", "AES"),
            WifiSecurity.AuthFcuWpa2Psk => ("WPA2", "AES"),
            _ => throw new ArgumentException($"不支持直连的认证类型 {authAlgorithm}", nameof(authAlgorithm)),
        };

        var needsKey = authToken != "open" && authToken != "shared";
        if (needsKey && string.IsNullOrEmpty(passphrase))
            throw new ArgumentException($"{authToken} 网络必须给口令", nameof(passphrase));
        if (needsKey && passphrase!.Contains('\0'))
            throw new ArgumentException("口令含非法字符", nameof(passphrase));

        var sb = new StringBuilder(512);
        sb.Append("<?xml version=\"1.0\"?>\r\n");
        sb.Append("<WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\">\r\n");
        AppendElement(sb, "name", ssid);
        sb.Append("  <SSIDConfig>\r\n    <SSID>\r\n");
        if (hidden) sb.Append("      <hidden>true</hidden>\r\n");
        AppendElement(sb, "name", ssid, "      ");
        sb.Append("    </SSID>\r\n  </SSIDConfig>\r\n");
        sb.Append("  <connectionType>ESS</connectionType>\r\n");
        // manual 而不是 auto：auto 会让 Windows 以后每次开机自动重连这个网络，
        // 用户在本程序里点一下"连接"不该产生这种长期副作用。
        sb.Append("  <connectionMode>manual</connectionMode>\r\n");
        if (hidden) sb.Append("  <nonBroadcast>true</nonBroadcast>\r\n");
        sb.Append("  <MSM>\r\n    <security>\r\n      <authEncryption>\r\n");
        sb.Append($"        <authentication>{authToken}</authentication>\r\n");
        sb.Append($"        <encryption>{encToken}</encryption>\r\n");
        sb.Append("        <useOneX>false</useOneX>\r\n");
        sb.Append("      </authEncryption>\r\n");
        if (needsKey)
        {
            sb.Append("      <sharedKey>\r\n        <keyType>networkKey</keyType>\r\n");
            sb.Append("        <protected>false</protected>\r\n");
            AppendElement(sb, "keyMaterial", passphrase!, "        ");
            sb.Append("      </sharedKey>\r\n");
        }
        sb.Append("    </security>\r\n  </MSM>\r\n");
        sb.Append("  <preSharedKeyActivationMode>StopIfDisconnectedAndActivate</preSharedKeyActivationMode>\r\n");
        sb.Append("</WLANProfile>\r\n");
        return sb.ToString();
    }

    private static void AppendElement(StringBuilder sb, string name, string value, string indent = "  ")
        => sb.Append(indent).Append('<').Append(name).Append('>')
           .Append(Escape(value)).Append("</").Append(name).Append(">\r\n");

    /// <summary>XML 文本转义。SSID 是外部输入，这里不转义就等于把结构交给网络名决定。</summary>
    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
