using System.Runtime.InteropServices;
using Beautiful.App.Services;

namespace Beautiful.App.Animation;

/// <summary>
/// 会话期接管系统"最小化到任务栏"动画（<c>SPI_SETANIMATION</c>）。
/// <para>
/// 【为什么必须接管】macOS 的最小化只有一种动画：窗口被吸进 Dock 图标。Windows 这边默认还有一条
/// "窗口缩成一个矩形飞向任务栏"的系统动画，两条同时跑就是用户说的"打架"：我们的替身正往 Dock 收，
/// 真窗口在底下又缩向任务栏，动画期间看起来像两块东西各自跑。系统那条我们不参与合成，
/// 唯一能关掉它的公开开关就是 <c>SPI_SETANIMATION.iMinAnimate = 0</c>。
/// </para>
/// <para>
/// 【为什么不写用户配置】<c>fWinIni</c> 传 0（既不写注册表也不广播 <c>WM_SETTINGCHANGE</c>）：
/// 这是一次<b>会话内的临时接管</b>，退出时必须还原成接管前的值。写进用户配置等于"装了个美化工具，
/// 顺手把系统的动画偏好永久改了"——那是越权。还原走同一 API，传回原值。
/// </para>
/// <para>
/// 【失败怎么处理】拿不到原值、或设不下去，一律只记 WARN 并放弃接管：<b>动画子系统不许因为
/// "关不掉系统动画"而影响窗口功能本身</b>（红线）。接管失败的表现只是"两条动画打架"，
/// 比"最小化坏了"轻得多。
/// </para>
/// </summary>
public sealed class SystemAnimationSuppressor : IDisposable
{
    private const uint SpiGetAnimation = 0x0042;
    private const uint SpiSetAnimation = 0x0043;

    /// <summary>ANIMATIONINFO：cbSize 由调用方填（8 = sizeof(UINT) + sizeof(int)）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref AnimationInfo info, uint winIni);

    private readonly object _gate = new();
    private int? _original;
    private bool _active;

    /// <summary>是否已接管（诊断行用）。</summary>
    public bool IsActive { get { lock (_gate) return _active; } }

    /// <summary>接管前的原值（0/1）；没接管成功时 null。</summary>
    public int? OriginalValue { get { lock (_gate) return _original; } }

    /// <summary>
    /// 关闭系统最小化动画。幂等：已接管时再调只回一行日志，不重复读原值
    /// （重复读会把"已经被我们改成 0"的值当成原值，退出时就还原不回去了）。
    /// </summary>
    public bool Enable()
    {
        lock (_gate)
        {
            if (_active) return true;

            var info = new AnimationInfo { Size = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = 0 };
            if (!SystemParametersInfo(SpiGetAnimation, info.Size, ref info, 0))
            {
                // 本机实测（Win11 24H2）：SPI_GETANIMATION 与 SPI_SETANIMATION 都返回 FALSE 且不写值
                // （哨兵值原样带回、GetLastWin32Error=0）——老式 SPI 在新系统上被限制。
                // 诚实的降级是：盲试一次 SET(0) 并用 GET 复核，两者都不响应就把结论写进日志，
                // 绝不假装"已接管"。
                var blind = new AnimationInfo { Size = info.Size, MinAnimate = 0 };
                var okSet = SystemParametersInfo(SpiSetAnimation, blind.Size, ref blind, 0);
                var verify = new AnimationInfo { Size = info.Size, MinAnimate = 42 };   // 哨兵：分辨"没写"与"写成0"
                var okVerify = SystemParametersInfo(SpiGetAnimation, verify.Size, ref verify, 0);
                if (okSet && okVerify && verify.MinAnimate == 0)
                {
                    // GET 被限制但 SET 生效了：系统动画已关，但我们拿不到原值。
                    // 还原时按"系统默认值 1"处理（Windows 出厂该项默认开），并把这个假设写进日志。
                    _original = 1;
                    _active = true;
                    Logger.Warn("系统动画接管：SPI_GETANIMATION 被系统限制，盲写 SET(0) 成功；"
                                + "退出时按出厂默认值 1 还原（如用户此前手动关过系统动画，退出后会回到开）");
                    return true;
                }

                Logger.Warn("系统动画接管：本机系统不响应 SPI_GETANIMATION/SPI_SETANIMATION"
                            + "（新系统限制，探针 tools/spi-probe 可复现），本次不接管——"
                            + "系统「缩向任务栏」动画可能与 genie 同时播放，属已知限制");
                return false;
            }

            var original = info.MinAnimate;
            if (original == 0)
            {
                // 系统本来就关着：记下来，退出时还它 0，不算我们改过
                _original = 0;
                _active = true;
                Logger.Info("系统动画接管：系统最小化动画本就是关闭状态，无需修改（退出时保持原样）");
                return true;
            }

            var set = new AnimationInfo { Size = info.Size, MinAnimate = 0 };
            if (!SystemParametersInfo(SpiSetAnimation, set.Size, ref set, 0))
            {
                Logger.Warn($"系统动画接管：写入 SPI_SETANIMATION 失败（Win32={Marshal.GetLastWin32Error()}），本次不接管");
                return false;
            }

            _original = original;
            _active = true;
            Logger.Info($"系统动画接管：已关闭系统最小化动画（原值={original}，仅本会话生效，退出还原）");
            return true;
        }
    }

    /// <summary>
    /// 还原原值。幂等；进程退出链路（ExitApp / OnExit）两处都会调，第二次是 no-op。
    /// 还原失败只记日志——退出过程中不能因为一次 API 失败卡住清理。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (!_active) return;
            _active = false;
            var original = _original;
            _original = null;
            if (original is null) return;

            var info = new AnimationInfo
            {
                Size = (uint)Marshal.SizeOf<AnimationInfo>(),
                MinAnimate = original.Value,
            };
            if (SystemParametersInfo(SpiSetAnimation, info.Size, ref info, 0))
                Logger.Info($"系统动画接管：已还原系统最小化动画（值={original.Value}）");
            else
                Logger.Warn($"系统动画接管：还原失败（Win32={Marshal.GetLastWin32Error()}），系统动画可能保持关闭到下次登录");
        }
    }
}
